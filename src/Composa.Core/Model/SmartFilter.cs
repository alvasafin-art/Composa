using Composa.Filters;
namespace Composa.Model;
public sealed record SmartFilter(Guid Id, FilterSettings Settings, bool Enabled = true);
