namespace YAVSOS.Vendor.MultiUserChest {
    public interface IPackage {
        ZPackage WriteToPackage();

#if DEBUG
        void PrintDebug();
#endif
    }
}
