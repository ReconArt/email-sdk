using System;

namespace ReconArt.Email.Sender.Internal
{
    internal static class ExceptionFilters
    {
        internal static bool DisposeWithoutUnwindingStack(params IDisposable[] disposables)
        {
            for (int i = 0; i < disposables.Length; i++)
            {
                disposables[i].Dispose();
            }

            // This is where the magic happens - we return false in order to NOT unwind the stack.
            return false;
        }
    }
}
