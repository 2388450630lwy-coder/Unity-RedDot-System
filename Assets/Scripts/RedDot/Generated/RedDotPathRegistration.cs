// AUTO-GENERATED
// 2026-06-15 22:31

namespace RedDot
{
    public static class RedDotPathsRegistration
    {
        public static void RegisterAll() { RegisterAll(RedDotManager.Instance); }
        public static void RegisterAll(RedDotManager mgr) { if (mgr == null) return;
            mgr.RegisterNode(RedDotPaths.Root, 0, true);
            mgr.RegisterNode(RedDotPaths.Root_Bag, RedDotPaths.Root, true);
            mgr.RegisterNode(RedDotPaths.Root_Mail, RedDotPaths.Root, true);
            mgr.RegisterNode(RedDotPaths.Root_Role, RedDotPaths.Root, true);
            mgr.RegisterNode(RedDotPaths.Root_Shop, RedDotPaths.Root, true);
            mgr.RegisterNode(RedDotPaths.Root_Bag_ItemCount, RedDotPaths.Root_Bag, true);
            mgr.RegisterNode(RedDotPaths.Root_Mail_Person, RedDotPaths.Root_Mail, true);
            mgr.RegisterNode(RedDotPaths.Root_Mail_System, RedDotPaths.Root_Mail, true);
            mgr.RegisterNode(RedDotPaths.Root_Role_AttrPoint, RedDotPaths.Root_Role, true);
            mgr.RegisterNode(RedDotPaths.Root_Role_Upgrade, RedDotPaths.Root_Role, true);
            mgr.RegisterNode(RedDotPaths.Root_Shop_Lottery, RedDotPaths.Root_Shop, true);
            mgr.RegisterNode(RedDotPaths.Root_Shop_Lottery_Adv, RedDotPaths.Root_Shop_Lottery, true);
            mgr.RegisterNode(RedDotPaths.Root_Shop_Lottery_Free, RedDotPaths.Root_Shop_Lottery, true);
        }
    }
}
