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
    }
}
