namespace Pinion.Engine.Model;

/// <summary>Canonical domain-sensitivity tag values used across the engine.</summary>
public static class DomainTag
{
    public const string Money = "money";
    public const string Auth = "auth";
    public const string Date = "date";
    public const string Io = "io";
    public const string DataTransform = "data-transform";
}

/// <summary>Canonical .NET Framework→Core migration-landmine values.</summary>
public static class MigrationLandmine
{
    public const string WebForms = "WebForms";
    public const string Wcf = "WCF";
    public const string Ef6 = "EF6";
}
