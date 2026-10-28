namespace YAVSOS.Vendor.MultiUserChest {
    public interface IRequest : IPackage {
        int RequestID { get; set; }
        Inventory SourceInventory { get; }
        Inventory TargetInventory { get; }
    }
}
