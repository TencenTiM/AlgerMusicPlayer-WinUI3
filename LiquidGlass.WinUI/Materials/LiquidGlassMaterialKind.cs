namespace LiquidGlass.WinUI.Materials;

/// <summary>
/// Liquid Glass（液态玻璃）组件材质类型枚举。
/// </summary>
public enum LiquidGlassMaterialKind
{
    /// <summary>
    /// 灵动坞/浮动胶囊（如底部悬浮播放栏、顶部搜索胶囊）：低模糊保留底色、较强边缘折射、明显顶部/底部高光棱线。
    /// </summary>
    Dock = 0,

    /// <summary>
    /// 内容卡片/文件夹容器（如歌单卡片、设置卡片、主内容大卡）：高模糊强磨砂、自然透色、柔和边缘反光与无接缝折射。
    /// </summary>
    Folder = 1,

    /// <summary>
    /// 次级胶囊按钮与图标按钮：中等模糊、轻盈反光边框与按压微弹。
    /// </summary>
    Button = 2,

    /// <summary>
    /// 导航栏与大面积侧边玻璃面板：高模糊稳定可读性、极轻折射避免视觉干扰。
    /// </summary>
    Panel = 3,

    /// <summary>
    /// 弹层、菜单与浮窗容器：较高模糊与不透明度以确保前景交互清晰。
    /// </summary>
    Popover = 4,

    /// <summary>
    /// 自定义参数材质。
    /// </summary>
    Custom = 5
}
