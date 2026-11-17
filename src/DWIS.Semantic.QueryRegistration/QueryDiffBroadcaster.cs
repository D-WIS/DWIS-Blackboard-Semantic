using System.Threading.Channels;

namespace DWIS.Semantic.QueryRegistration;

/// <summary>Thrown into a subscription that fell too far behind: the subscriber registers again and gets a full snapshot.</summary>
public sealed class SubscriberTooSlowException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SubscriberTooSlowException()
        : base($"The subscriber fell more than {QueryDiffBroadcaster.Capacity} diffs behind; register again for a full snapshot.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SubscriberTooSlowException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public SubscriberTooSlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The sink that fans the diffs of every round out to the live subscribers: the <c>WatchQueries</c> streams of
/// the real-time processes and the server-sent events of the HTTP subscribers (specification 3.4, 3.5). Each
/// subscription is a bounded queue; a round never waits for a slow subscriber: a subscription that is full is
/// ended with <see cref="SubscriberTooSlowException"/>, and the subscriber registers again for a full snapshot.
/// </summary>
public sealed class QueryDiffBroadcaster : IQueryDiffSink
{
    /// <summary>Diffs a subscriber may fall behind before its subscription is ended.</summary>
    public const int Capacity = 10_000;

    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>The live subscriptions.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Opens a subscription to the diffs that pass the filter (all of them when null); dispose it to end it.</summary>
    public Subscription Subscribe(Func<QueryDiff, bool>? filter = null)
    {
        var subscription = new Subscription(this, filter);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <inheritdoc />
    public Task PublishAsync(QueryDiff diff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diff);
        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Offer(diff);
        }

        return Task.CompletedTask;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>One subscriber's queue of diffs.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly QueryDiffBroadcaster _owner;
        private readonly Func<QueryDiff, bool>? _filter;
        private readonly Channel<QueryDiff> _channel = Channel.CreateBounded<QueryDiff>(new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        internal Subscription(QueryDiffBroadcaster owner, Func<QueryDiff, bool>? filter)
        {
            _owner = owner;
            _filter = filter;
        }

        /// <summary>The diffs, in the order of the rounds; the reader ends when the subscription is disposed, or faults when it fell behind.</summary>
        public ChannelReader<QueryDiff> Diffs => _channel.Reader;

        internal void Offer(QueryDiff diff)
        {
            if (_filter is not null && !_filter(diff))
            {
                return;
            }

            if (!_channel.Writer.TryWrite(diff))
            {
                _channel.Writer.TryComplete(new SubscriberTooSlowException());
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
