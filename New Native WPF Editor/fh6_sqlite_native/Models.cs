namespace FH6SQLiteEditorNative;

public sealed record CarListItem(long Id, string Title, string Subtitle)
{
    public override string ToString() => Title;
}

public sealed record TableChoice(string Name)
{
    public override string ToString() => Name;
}

public sealed record EngineChoice(long EngineId, string Label)
{
    public override string ToString() => Label;
}

public sealed record TemplateChoice(string SourceTable, long SourceId, string Label)
{
    public override string ToString() => Label;
}

public sealed record AspirationConversionChoice(long AspirationId, string TableName, string PartName, string Label, long ExistingRows, bool IsStock)
{
    public override string ToString() => Label;
}

public sealed record MenuDisplayChoice(long UpgradeId, string PartName, long Level, string Label, string IconPath, string ImagePath)
{
    public override string ToString() => Label;
}

public sealed record AeroPartChoice(string TableName, string Label)
{
    public override string ToString() => Label;
}

internal sealed record ColumnInfo(string Name, string Type, int PrimaryKeyRank);

internal sealed record LocalTable(string Name, long RowCount, bool Changed);
