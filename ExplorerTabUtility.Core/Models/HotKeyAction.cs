using System.ComponentModel;
using System.Text.Json.Serialization;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Models;

[JsonConverter(typeof(HotKeyActionJsonConverter))]
public enum HotKeyAction
{
    [Description("打开")]
    Open,
    [Description("复制标签页")]
    Duplicate,
    [Description("重新打开已关闭")]
    ReopenClosed,
    [Description("标签搜索")]
    TabSearch,
    [Description("后退")]
    NavigateBack,
    [Description("向上")]
    NavigateUp,
    [Description("前进")]
    NavigateForward,
    [Description("设置目标窗口")]
    SetTargetWindow,
    [Description("切换窗口拦截")]
    ToggleWinHook,
    [Description("切换标签复用")]
    ToggleReuseTabs,
    [Description("显示/隐藏")]
    ToggleVisibility,
    [Description("分离标签页")]
    DetachTab,
    [Description("贴靠至右侧")]
    SnapRight,
    [Description("贴靠至左侧")]
    SnapLeft,
    [Description("贴靠至顶部")]
    SnapUp,
    [Description("贴靠至底部")]
    SnapDown
}
