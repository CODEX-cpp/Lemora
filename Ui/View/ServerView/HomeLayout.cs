namespace Lemora.View.ServerView
{
    /// <summary>
    /// Home layout according to the window width (responsive Home).
    /// The layout changes, not the content:
    /// Wide   (>= 1100): folders with names, list, fixed detail on the right;
    /// Medium (800-1099): icon-only folders, detail overlaid on the right when an entry is chosen;
    /// Small  (560-799): folders in the drawer menu (button with three lines), overlaid detail, single-line rows;
    /// Square (< 560): like Small, but the detail takes the place of the list and rows do not show the user.
    /// </summary>
    public enum HomeLayoutMode { Wide, Medium, Small, Square }

    public static class HomeLayout
    {
        // blueprint thresholds; applied to the page width (almost the same as the window's)
        public static HomeLayoutMode For(double width) =>
            width >= 1100 ? HomeLayoutMode.Wide :
            width >= 800 ? HomeLayoutMode.Medium :
            width >= 560 ? HomeLayoutMode.Small : HomeLayoutMode.Square;

        /// <summary>search width when it does not fill the free space</summary>
        public static double SearchWidth(HomeLayoutMode mode) => mode == HomeLayoutMode.Wide ? 440 : 340;
    }
}
