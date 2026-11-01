using SQLite;

namespace CarrisRebuild.Services;

// Favoritos offline (DbStop/DbRoute observados) via SQLite, como a original.
public sealed class FavoriteStop
{
    [PrimaryKey] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
public sealed class FavoriteRoute
{
    [PrimaryKey] public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    [Ignore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Carreira" : Name;
}
public sealed class FavoritesService(string dbPath)
{
    private readonly SQLiteAsyncConnection _db = new(dbPath);

    public async Task InitAsync()
    {
        await _db.CreateTableAsync<FavoriteStop>();
        await _db.CreateTableAsync<FavoriteRoute>();
    }

    public Task<List<FavoriteStop>> AllStopsAsync() => _db.Table<FavoriteStop>().ToListAsync();
    public Task<List<FavoriteRoute>> AllRoutesAsync() => _db.Table<FavoriteRoute>().ToListAsync();

    public async Task<bool> IsStopFavAsync(string id) => await _db.FindAsync<FavoriteStop>(id) is not null;
    public async Task<bool> IsRouteFavAsync(string number) => await _db.FindAsync<FavoriteRoute>(number) is not null;

    // Devolve true se ficou guardado, false se foi removido.
    public async Task<bool> ToggleStopAsync(string id, string name)
    {
        if (await _db.FindAsync<FavoriteStop>(id) is not null)
        {
            await _db.DeleteAsync<FavoriteStop>(id);
            return false;
        }
        await _db.InsertAsync(new FavoriteStop { Id = id, Name = name });
        return true;
    }

    public async Task<bool> ToggleRouteAsync(string number, string name)
    {
        if (await _db.FindAsync<FavoriteRoute>(number) is not null)
        {
            await _db.DeleteAsync<FavoriteRoute>(number);
            return false;
        }
        await _db.InsertAsync(new FavoriteRoute { Number = number, Name = name });
        return true;
    }
}
