namespace MadClean.Core;

/// <summary>Current-user package identity observations, not unique application or action eligibility evidence.</summary>
public sealed record PackagedApplication(string Name, string FullName, string FamilyName, string Version,
    string Publisher, string Architecture, string PackageType, bool? NonRemovable, bool? IsFramework,
    bool? IsResourcePackage, string SignatureKind);

public interface IPackagedApplicationInventory
{
    Task<CheckResult<PackagedApplication>> ReadAsync(CancellationToken token);
}
