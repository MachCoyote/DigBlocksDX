using UnityEngine;

namespace DigBlocks.Client.UI
{
    //the persistent client UI hierarchy menus are attached to
    public interface IUIRoot
    {
        GameObject Root { get; }

        Transform GetLayer(MenuLayer layer);
    }
}
