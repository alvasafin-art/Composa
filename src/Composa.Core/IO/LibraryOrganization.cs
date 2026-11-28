namespace Composa.IO;

public sealed record LibraryGroup(Guid Id, string Name, bool Collapsed, Guid[] Resources);

public sealed record LibraryOrganization(Guid[] Order, LibraryGroup[] Groups)
{
    public static LibraryOrganization Empty { get; } = new([], []);

    public LibraryOrganization WithEntries(IEnumerable<LibraryEntry> entries)
    {
        var ids = entries.Select(entry => entry.Id).ToArray();
        var available = ids.ToHashSet();
        var claimed = new HashSet<Guid>();
        var groupIds = new HashSet<Guid>();
        var groups = Groups.Where(group => group != null && group.Id != Guid.Empty && !available.Contains(group.Id)
                && groupIds.Add(group.Id) && ElementLibrary.NormalizeName(group.Name) != null)
            .Select(group => group with {
                Resources = (group.Resources ?? []).Where(id => available.Contains(id) && claimed.Add(id)).ToArray()
            }).ToArray();
        var root = ids.Where(id => !claimed.Contains(id)).Concat(groups.Select(group => group.Id)).ToArray();
        var rootIds = root.ToHashSet();
        return new(Order.Concat(root).Where(rootIds.Contains).Distinct().ToArray(), groups);
    }
}
