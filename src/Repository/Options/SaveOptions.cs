using System.Diagnostics.CodeAnalysis;

namespace eQuantic.Core.Data.Repository.Options;

[ExcludeFromCodeCoverage]
public class SaveOptions
{
    private bool _saveAuditMetadata;
    private object _userId;
    
    public SaveOptions WithAuditMetadata<TUserKey>(TUserKey userId)
    {
        _saveAuditMetadata = true;
        _userId = userId;
        
        return this;
    }

    public bool IsAuditMetadata() => _saveAuditMetadata;
    public TUserKey GetUserId<TUserKey>() => (TUserKey)_userId;
}
