using System.Text.Json;
using YardStickOne.Api.Models;

namespace YardStickOne.Web.Services;

/// <summary>
/// Persistence helpers for named RF captures, stored in <c>captures.json</c>
/// in the application's current directory.
/// </summary>
public static class SavedCaptures
{
	private static readonly string FilePath =
		Path.Combine(AppContext.BaseDirectory, "captures.json");

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
	};

	public static async Task<List<SavedCapture>> LoadAsync()
	{
		if (!File.Exists(FilePath)) return [];

		await using var fs = File.OpenRead(FilePath);
		return await JsonSerializer.DeserializeAsync<List<SavedCapture>>(fs, JsonOptions)
			   ?? [];
	}

	public static async Task SaveAsync(string name, RfSignal signal)
	{
		var all = await LoadAsync();
		all.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
		all.Add(new SavedCapture(name, signal));
		all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

		await using var fs = File.Create(FilePath);
		await JsonSerializer.SerializeAsync(fs, all, JsonOptions);
	}
}

public sealed record SavedCapture(string Name, RfSignal Signal);
