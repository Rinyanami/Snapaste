namespace Snapaste.Ocr;

internal static class OcrMessages
{
    public const string EmptySelection = "选区没有可识别的图片。";
    public const string Loading = "正在加载离线文字识别模型…";
    public const string Progress = "正在识别图片";
    public const string Complete = "已识别图片文字";
    public const string MissingModel = "缺少离线文字识别模型。请重新解压完整的 Snapaste 便携包。";
    public const string LoadFailed = "无法加载离线文字识别组件。请重新解压完整的 Snapaste 便携包。";
    public const string Memory = "无法分配文字识别所需的图片内存。";

    public static string TileProgress(int current, int total) => $"正在识别第 {current} / {total} 段";
    public static string LineProgress(string label, int current, int total) => $"{label}（{current} / {total} 行）";
}
