namespace DigBlocks.Client.UI
{
    public enum MenuBackBehavior
    {
        //back is absorbed and does nothing
        None,

        //back closes the menu
        Close,

        //the view handles back through IMenuBackHandler
        ViewHandled
    }
}
