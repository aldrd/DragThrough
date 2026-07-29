#nullable enable
using System;
using System.Diagnostics;

namespace ZombieBar.Utilities
{
    /// <summary>
    /// "Share the app" targets and the logic that opens each network's web share intent (or copies
    /// the project link). Shared by the tray flyout's Share popup. Each target carries the resource
    /// key + fallback for its localized label.
    /// </summary>
    public static class AppShare
    {
        public sealed record Target(string Kind, string Key, string Fallback);

        // "copy" first (the popup draws a separator after it), then the most-used networks worldwide.
        public static readonly Target[] Targets =
        {
            new Target("copy",     "share_copy_link", "Copy link"),
            new Target("x",        "share_tweet",     "Tweet (X)"),
            new Target("telegram", "share_telegram",  "Post on Telegram"),
            new Target("whatsapp", "share_whatsapp",  "WhatsApp"),
            new Target("facebook", "share_facebook",  "Facebook"),
            new Target("linkedin", "share_linkedin",  "LinkedIn"),
            new Target("reddit",   "share_reddit",    "Reddit"),
            new Target("email",    "share_email",     "Email"),
        };

        /// <summary>
        /// Copies the project link (kind == "copy") or opens the chosen network's web share intent.
        /// Returns true when it copied the link, so the caller can show "copied" feedback.
        /// </summary>
        public static bool Share(string kind)
        {
            string url = AppLinks.ProjectUrl;
            // Shared blurb: the app name (the header / tray title) plus the About description.
            string name = Loc("tray_tooltip", "DragThrough");
            string tagline = Loc("about_tagline", "Windows helpers app");
            string text = $"{name} — {tagline}";

            if (kind == "copy")
            {
                try { System.Windows.Clipboard.SetText(url); } catch { /* clipboard may be busy */ }
                return true;
            }

            string E(string s) => Uri.EscapeDataString(s);
            string? target = kind switch
            {
                "x"        => $"https://twitter.com/intent/tweet?text={E(text)}&url={E(url)}",
                "telegram" => $"https://t.me/share/url?url={E(url)}&text={E(text)}",
                "whatsapp" => $"https://wa.me/?text={E(text + " " + url)}",
                "facebook" => $"https://www.facebook.com/sharer/sharer.php?u={E(url)}",
                "linkedin" => $"https://www.linkedin.com/sharing/share-offsite/?url={E(url)}",
                "reddit"   => $"https://www.reddit.com/submit?url={E(url)}&title={E(text)}",
                "email"    => $"mailto:?subject={E(text)}&body={E(text + "\n\n" + url)}",
                _ => null
            };

            if (target != null)
            {
                OpenUrl(target);
            }

            return false;
        }

        /// <summary>Opens a URL in the user's default browser.</summary>
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static string Loc(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
