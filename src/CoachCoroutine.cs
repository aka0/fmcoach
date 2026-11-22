using System;
using System.Collections;
using System.Collections.Generic;

internal static class CoachCoroutine
{
    // Cached/empty nested iterators must not run an entire traversal in one Unity update.
    internal static IEnumerator Flatten(IEnumerator source, int transitionsPerFrame = 32)
    {
        if (transitionsPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(transitionsPerFrame));
        var stack = new Stack<IEnumerator>(); stack.Push(source);
        int transitions = 0;
        while (stack.Count > 0)
        {
            if (transitions++ >= transitionsPerFrame) { transitions = 0; yield return null; }
            var top = stack.Peek();
            if (!top.MoveNext()) { stack.Pop(); continue; }
            if (top.Current is IEnumerator nested) stack.Push(nested);
            else { transitions = 0; yield return null; }
        }
    }

    internal static float BatchWaitSeconds(float callbackSeconds, int count, int concurrentLimit) =>
        Math.Max(30f, callbackSeconds * Math.Max(1, (int)Math.Ceiling(count / (double)concurrentLimit)) + 10f);
}
