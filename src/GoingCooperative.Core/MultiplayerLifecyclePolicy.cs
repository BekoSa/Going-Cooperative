namespace GoingCooperative.Core
{
    public static class MultiplayerLifecyclePolicy
    {
        public static bool IsCurrentPeerWork(
            bool peerClosed,
            long currentConnectionGeneration,
            long workConnectionGeneration)
        {
            return !peerClosed
                && currentConnectionGeneration > 0L
                && currentConnectionGeneration == workConnectionGeneration;
        }

        public static bool IsHostWorldReady(
            int loadGeneration,
            int resumeGeneration)
        {
            return loadGeneration <= resumeGeneration;
        }

        public static bool IsPeerGameplayReady(
            bool peerClosed,
            bool readyForReplication,
            bool catchupPending,
            bool worldLoaded)
        {
            return !peerClosed
                && readyForReplication
                && !catchupPending
                && worldLoaded;
        }

        public static bool ShouldApplyDisconnectedPeerCleanup(
            bool currentPeerConnected)
        {
            // Disconnect notifications are deferred from the control worker to the
            // Unity thread. A reconnect may reuse the same peer slot before that
            // notification is drained. In that case the peer-id keyed replication
            // state already belongs to the replacement connection and must survive.
            return !currentPeerConnected;
        }
    }
}
