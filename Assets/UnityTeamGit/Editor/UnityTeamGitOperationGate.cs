using System.Threading;

namespace Blackbox.UnityTeamGit.Editor
{
    internal static class UnityTeamGitOperationGate
    {
        private static int active;

        public static bool IsBusy
        {
            get { return Volatile.Read(ref active) != 0; }
        }

        public static bool TryEnter()
        {
            return Interlocked.CompareExchange(ref active, 1, 0) == 0;
        }

        public static void Exit()
        {
            Interlocked.Exchange(ref active, 0);
        }
    }
}
