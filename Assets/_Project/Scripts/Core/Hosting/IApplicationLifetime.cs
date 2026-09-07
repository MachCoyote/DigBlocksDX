namespace DigBlocks.Core.Hosting
{
    //abstracts process shutdown so application flow stays testable
    public interface IApplicationLifetime
    {
        void RequestQuit();
    }
}
