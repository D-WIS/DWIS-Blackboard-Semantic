namespace DWIS.Semantic.QueryRegistration.Tests;

/// <summary>
/// The 12 tests of the old server's <c>tests\DWIS.QueryLifecycle.Tests</c>, ported unchanged in intent (test plan
/// G.3, T-QR-1): registration per text and per owner, release, snapshot, diff, rounds, failures.
/// </summary>
public sealed class QueryManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Query = "SELECT ?s WHERE { ?s <urn:predicate> <urn:object> . }";

    private static QueryRows Result(params string[] ids) => new(["s"], ids.Select(id => new QueryRow("urn:test/" + id)));

    private static QueryEvaluator Answering(Func<string, QueryRows?> answer) => (query, _) => Task.FromResult(answer(query));

    [Fact]
    public async Task Concurrent_duplicates_and_repeated_calls_do_not_add_registrations_or_ownership()
    {
        var evaluations = 0;
        var manager = new QueryManager(Answering(_ => { Interlocked.Increment(ref evaluations); return Result("one"); }));
        var ids = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => manager.Register(Query, "owner"), Ct)));
        ids.Distinct().Should().ContainSingle();
        manager.Count.Should().Be(1);
        await manager.ResolveAllAsync(Ct);
        evaluations.Should().Be(1);
        manager.ReleaseOwner("owner").Should().ContainSingle();
        manager.Count.Should().Be(0);
    }

    [Fact]
    public async Task Shared_queries_survive_until_their_last_owner_closes()
    {
        var manager = new QueryManager(Answering(_ => Result("one")));
        var id = manager.Register(Query, "A");
        manager.Register(Query, "B").Should().Be(id);
        manager.ReleaseOwner("A").Should().BeEmpty();
        manager.Count.Should().Be(1);
        (await manager.SnapshotAsync(id, Ct))!.Added.Should().ContainSingle();
        manager.Unregister(Query, "outsider", out _).Should().BeFalse();
        manager.Unregister(Query, "B", out var released).Should().BeTrue();
        released.Should().Be(id);
        manager.Count.Should().Be(0);
        (await manager.ResolveAsync(id, cancellationToken: Ct)).Should().BeNull();
        manager.ReleaseOwner("B").Should().BeEmpty();
    }

    [Fact]
    public async Task Every_subscriber_receives_a_full_snapshot_without_consuming_pending_changes()
    {
        var current = Result("one");
        var manager = new QueryManager(Answering(_ => current));
        var id = manager.Register(Query, "A");
        (await manager.SnapshotAsync(id, Ct))!.Added.Should().ContainSingle();
        current = Result("one", "two");
        manager.Register(Query, "B");
        (await manager.SnapshotAsync(id, Ct))!.Added.Should().HaveCount(2);
        var pending = (await manager.ResolveAsync(id, cancellationToken: Ct))!;
        pending.Added.Should().ContainSingle().Which[0].Should().Be("urn:test/two");
        (await manager.ResolveAsync(id, cancellationToken: Ct))!.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Session_release_preserves_internal_registrations_and_other_queries()
    {
        var manager = new QueryManager(Answering(_ => Result()));
        var permanent = manager.Register(Query);
        manager.Register(Query, "A");
        manager.Register(Query + " # other", "A");
        manager.ReleaseOwner("A").Should().ContainSingle();
        manager.Count.Should().Be(1);
        (await manager.ResolveAsync(permanent, cancellationToken: Ct)).Should().NotBeNull();
    }

    [Fact]
    public void Normalization_does_not_change_literal_meaning()
    {
        var manager = new QueryManager(Answering(_ => Result()));
        var id = manager.Register("  " + Query + "\r\n", "A");
        manager.Register(Query, "A").Should().Be(id);
        manager.Register("SELECT ?s WHERE { ?s <urn:p> \"a b\" . }", "A")
            .Should().NotBe(manager.Register("SELECT ?s WHERE { ?s <urn:p> \"a  b\" . }", "A"));
        var register = () => manager.Register(" ", "A");
        register.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Release_during_evaluation_does_not_resurrect_a_removed_entry()
    {
        using var entered = new SemaphoreSlim(0);
        using var complete = new SemaphoreSlim(0);
        var manager = new QueryManager(async (_, ct) =>
        {
            entered.Release();
            (await complete.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().BeTrue();
            return Result("one");
        });
        var id = manager.Register(Query, "A");
        var resolving = Task.Run(() => manager.ResolveAllAsync(Ct), Ct);
        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeTrue();
            manager.ReleaseOwner("A");
        }
        finally
        {
            complete.Release();
        }

        (await resolving).Diffs.Should().BeEmpty();
        manager.Count.Should().Be(0);
        manager.Register(Query, "B").Should().NotBe(id);
    }

    [Fact]
    public async Task Failed_resolution_does_not_erase_the_previous_result()
    {
        QueryRows? current = Result("one");
        var manager = new QueryManager(Answering(_ => current));
        var id = manager.Register(Query, "A");
        await manager.SnapshotAsync(id, Ct);
        current = null;
        var resolve = () => manager.ResolveAsync(id, cancellationToken: Ct);
        await resolve.Should().ThrowAsync<InvalidOperationException>();
        current = Result("one", "two");
        (await manager.ResolveAsync(id, cancellationToken: Ct))!.Added.Should().ContainSingle();
    }

    [Fact]
    public async Task One_failed_query_does_not_block_other_subscribers_or_erase_its_baseline()
    {
        bool fail = false, changed = false;
        var manager = new QueryManager(Answering(query => query == "bad" && fail ? null : changed ? Result("one", "two") : Result("one")));
        int bad = manager.Register("bad", "A"), good = manager.Register("good", "B");
        await manager.SnapshotAsync(bad, Ct);
        await manager.SnapshotAsync(good, Ct);
        fail = true;
        changed = true;
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().ContainSingle().Which.QueryId.Should().Be(good);
        fail = false;
        var recovered = (await manager.ResolveAllAsync(Ct)).Diffs.Should().ContainSingle().Which;
        recovered.QueryId.Should().Be(bad);
        recovered.Added.Should().ContainSingle();
    }

    // The case behind this class: an application registers its query, crashes, restarts and registers
    // the same query again while its old session is still open on the server.
    [Fact]
    public async Task A_restarted_client_adds_no_entry_and_still_gets_the_full_result_and_the_changes()
    {
        var current = Result("one");
        var evaluations = 0;
        var manager = new QueryManager(Answering(_ => { evaluations++; return current; }));
        var before = manager.Register(Query, "session before the crash");
        (await manager.SnapshotAsync(before, Ct))!.Added.Should().ContainSingle();
        var after = manager.Register(Query, "session after the restart");
        after.Should().Be(before);
        manager.Count.Should().Be(1);
        // With the hash of the text as identifier and a diff as first answer, this answer was empty.
        (await manager.SnapshotAsync(after, Ct))!.Added.Should().ContainSingle();
        current = Result("one", "two");
        evaluations = 0;
        var changes = (await manager.ResolveAllAsync(Ct)).Diffs.Should().ContainSingle().Which;
        evaluations.Should().Be(1);
        changes.Added.Should().ContainSingle().Which[0].Should().Be("urn:test/two");
        // The old session times out: the entry stays, for the restarted client.
        manager.ReleaseOwner("session before the crash").Should().BeEmpty();
        manager.Count.Should().Be(1);
        current = Result("two");
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().ContainSingle().Which.Removed.Should().ContainSingle();
        manager.ReleaseOwner("session after the restart").Should().ContainSingle();
        manager.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_round_answers_all_registered_queries_from_one_start()
    {
        int starts = 0, inTheRound = 0, alone = 0;
        var current = Result("one");
        var manager = new QueryManager(
            Answering(_ => { alone++; return current; }),
            _ => { starts++; return Task.FromResult<QueryEvaluator>(Answering(_ => { inTheRound++; return current; })); });
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().BeEmpty();
        starts.Should().Be(0, "nothing registered: no round is started");
        foreach (var query in new[] { "a", "b", "c" })
        {
            await manager.SnapshotAsync(manager.Register(query, "A"), Ct);
        }

        alone.Should().Be(3, "a first answer is not part of a round");
        current = Result("one", "two");
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().HaveCount(3);
        starts.Should().Be(1);
        inTheRound.Should().Be(3);
        alone.Should().Be(3);
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().BeEmpty();
        starts.Should().Be(2);
    }

    [Fact]
    public async Task A_round_still_works_when_it_cannot_be_started_as_one()
    {
        var current = Result("one");
        var manager = new QueryManager(Answering(_ => current), _ => throw new InvalidOperationException("no start"));
        var id = manager.Register(Query, "A");
        await manager.SnapshotAsync(id, Ct);
        current = Result("one", "two");
        (await manager.ResolveAllAsync(Ct)).Diffs.Should().ContainSingle().Which.Added.Should().ContainSingle();
    }

    // A first answer made during a round would come from a newer state than the round's; if the round
    // then stored its older result, clients would be told that rows are gone when they are not.
    [Fact]
    public async Task A_first_answer_waits_for_the_running_round()
    {
        using var entered = new SemaphoreSlim(0);
        using var complete = new SemaphoreSlim(0);
        var block = false;
        var manager = new QueryManager(
            Answering(_ => Result("one")),
            _ => Task.FromResult<QueryEvaluator>(async (_, ct) =>
            {
                if (block)
                {
                    entered.Release();
                    (await complete.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().BeTrue();
                }

                return Result("one");
            }));
        var id = manager.Register(Query, "A");
        await manager.SnapshotAsync(id, Ct);
        block = true;
        var round = Task.Run(() => manager.ResolveAllAsync(Ct), Ct);
        Task<QueryDiff?>? firstAnswer = null;
        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeTrue();
            firstAnswer = Task.Run(() => manager.SnapshotAsync(id, Ct), Ct);
            (await Task.WhenAny(firstAnswer, Task.Delay(300, Ct))).Should().NotBeSameAs(firstAnswer, "the first answer waits for the round");
        }
        finally
        {
            complete.Release();
        }

        await round;
        (await firstAnswer!)!.Added.Should().ContainSingle();
    }
}
