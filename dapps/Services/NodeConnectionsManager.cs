namespace dapps.Services;

internal class NodeConnectionsManager
{
    private readonly ILogger<NodeConnectionsManager> logger;

    public NodeConnectionsManager(ILogger<NodeConnectionsManager> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// This method is called when there is one or more message for the specified
    /// destination node present in our database, and we should try to deliver it.
    /// </summary>
    /// <param name="destCallsign">The destination node for the message</param>
    internal Task SignalMessageReceivedFor(string destCallsign)
    {
        // Remember this is not just for nodes which are direct neighbours, but also for
        // nodes which claim to be able to forward for this destination node.

        logger.LogInformation($"{nameof(SignalMessageReceivedFor)}(destCallsign: {destCallsign})");
        return Task.CompletedTask;
    }
}