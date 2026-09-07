namespace DigBlocks.Client.UI
{
    public enum MenuRetentionPolicy
    {
        //the instance is destroyed when the menu closes
        Destroy,

        //the instance is kept disabled and reused the next time the menu opens
        Retain
    }
}
