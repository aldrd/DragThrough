#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// Animates a <see cref="FrameworkElement"/>'s <see cref="FrameworkElement.Width"/> toward an
    /// attached target width. Task buttons use it so they:
    ///   - grow in from zero when a window opens (0 -> full),
    ///   - resize smoothly when the bar rebalances or a button toggles compact,
    /// and — because the hosting WrapPanel re-measures/re-arranges as the width changes — the
    /// neighbouring buttons slide along smoothly instead of jumping.
    ///
    /// It drives Width (a layout property), never a RenderTransform, so it never fights the drag-reorder
    /// handler, which animates the container RenderTransforms during a gesture.
    /// </summary>
    public static class AnimatedWidth
    {
        private static readonly Duration AnimDuration = new Duration(TimeSpan.FromMilliseconds(120));

        // Data items whose button has already grown in once. The task list re-creates its button
        // containers on every filter Refresh (which happens on many events); without this, each
        // regenerated button would grow from zero again and the whole bar would flash. Keyed weakly by
        // the data item (the ApplicationWindow), so a closed window's entry is collected automatically
        // and a genuinely new window grows in exactly once.
        private static readonly ConditionalWeakTable<object, object> _grownItems = new();
        private static readonly object _grownMarker = new();

        /// <summary>Target width the element animates toward whenever this value changes.</summary>
        public static readonly DependencyProperty TargetWidthProperty =
            DependencyProperty.RegisterAttached(
                "TargetWidth", typeof(double), typeof(AnimatedWidth),
                new PropertyMetadata(double.NaN, OnTargetWidthChanged));

        public static double GetTargetWidth(DependencyObject o) => (double)o.GetValue(TargetWidthProperty);
        public static void SetTargetWidth(DependencyObject o, double v) => o.SetValue(TargetWidthProperty, v);

        private static void OnTargetWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe)
                return;

            double target = (double)e.NewValue;
            if (double.IsNaN(target))
                return;

            double from;
            bool firstAssignment = double.IsNaN((double)e.OldValue);
            if (firstAssignment)
            {
                // A brand-new item grows in from zero; a container regenerated for an item that has
                // already appeared (e.g. after a filter Refresh) must snap to its width instead, or the
                // whole bar flashes on every refresh.
                object item = fe.DataContext;
                bool alreadyGrown = item != null && _grownItems.TryGetValue(item, out _);
                if (item != null)
                    _grownItems.AddOrUpdate(item, _grownMarker);

                if (alreadyGrown)
                {
                    fe.BeginAnimation(FrameworkElement.WidthProperty, null);
                    fe.Width = target;
                    return;
                }

                from = 0;
            }
            else
            {
                // Rebalance / compact toggle: ease from the current rendered width.
                from = fe.ActualWidth > 0 ? fe.ActualWidth : (double)e.OldValue;
            }

            if (Math.Abs(from - target) < 0.5)
            {
                // No meaningful change: settle exactly on the target without a (visually pointless) tween.
                fe.BeginAnimation(FrameworkElement.WidthProperty, null);
                fe.Width = target;
                return;
            }

            var anim = new DoubleAnimation
            {
                From = from,
                To = target,
                Duration = AnimDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fe.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }
    }
}
