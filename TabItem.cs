namespace FSHE;

/// <summary>
/// 顶部标签页数据模型。
/// </summary>
public class TabItem
{
    public string Title { get; set; } = "FSHE";
    public string Path { get; set; } = string.Empty;

    public TabItem() { }

    public TabItem(string title, string path)
    {
        Title = title;
        Path = path;
    }
}
