using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckMotion()
    {
        var target = C<FrameworkElement>("AppsPage");
        try
        {
            motion.TestEnabled = true;
            motion.Enter(target);
            await Task.Delay(300);
            if (Math.Abs(target.Opacity - 1) > .001 || target.RenderTransform is not TranslateTransform transform || Math.Abs(transform.Y) > .001)
                throw new InvalidOperationException("Navigation motion did not finish in its resting state");
            motion.Enter(target, horizontal: true);
            motion.Enter(target, backwards: true, horizontal: true);
            motion.Stop(target);
            if (target.Opacity != 1 || ((TranslateTransform)target.RenderTransform).X != 0)
                throw new InvalidOperationException("Interrupted navigation left a stale visual state");
            motion.TestEnabled = false;
            motion.Enter(target);
            if (target.Opacity != 1)
                throw new InvalidOperationException("Reduced-motion mode started an animation");
            File.WriteAllText(Path.Combine(Store.Root, "design-preview", "motion-checks.json"), "{\"Passed\":3,\"completedAtRest\":true,\"rapidNavigationRestored\":true,\"reducedMotionRespected\":true}");
        }
        finally { motion.Stop(target); motion.TestEnabled = null; }
    }
}
