using Microsoft.EntityFrameworkCore;
using Npgsql;
using PixelHub.Api.Data;
using PixelHub.Api.Models;
using PixelHub.Api.Repositories;

namespace PixelHub.Api.Services;

public enum AchatStatut { Ok, JeuInconnu, JoueurInconnu, SoldeInsuffisant, DejaPossede }
public record AchatRecu(string Pseudo, string Titre, int PrixPaye, int Solde, bool ProjectionsAJour);
public record AchatResultat(AchatStatut Statut, AchatRecu? Recu = null);

// Une action métier, quatre moteurs : on lit le prix (MongoDB), on encaisse (PostgreSQL),
// puis on met à jour les projections (Neo4j, Redis). L'ordre n'est pas un détail (mission 4).
public class ShopService
{
    private readonly PixelHubContext _db;
    private readonly IGameCatalog _catalog;
    private readonly ISocialGraph _graph;
    private readonly RedisSales _ventes;
    private readonly ILogger<ShopService> _log;

    public ShopService(PixelHubContext db, IGameCatalog catalog, ISocialGraph graph,
                       RedisSales ventes, ILogger<ShopService> log)
    {
        _db = db;
        _catalog = catalog;
        _graph = graph;
        _ventes = ventes;
        _log = log;
    }

    public async Task<AchatResultat> BuyAsync(string pseudo, string titre)
    {
        // 1. MongoDB — le catalogue est la référence du prix COURANT
        var jeu = await _catalog.GetByTitleAsync(titre);
        if (jeu is null) return new(AchatStatut.JeuInconnu);

        var joueur = await _db.Players.SingleOrDefaultAsync(p => p.Pseudo == pseudo);
        if (joueur is null) return new(AchatStatut.JoueurInconnu);

        // Message clair pour le cas courant (le joueur réessaie). Ce test ne GARANTIT rien :
        // deux requêtes simultanées le passent toutes les deux. La garantie, c'est l'index unique, plus bas.
        if (await _db.Purchases.AnyAsync(a => a.PlayerId == joueur.Id && a.Titre == jeu.Titre))
            return new(AchatStatut.DejaPossede);

        // 2. PostgreSQL — le débit et la trace de l'achat : tout ou rien (ACID)
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            // Le test du solde et la soustraction dans le MÊME UPDATE : deux achats simultanés
            // ne peuvent pas lire tous les deux « 1200 » puis débiter tous les deux (TP 1, Q10).
            var lignes = await _db.Players
                .Where(p => p.Pseudo == pseudo)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Coins, p => p.Coins - jeu.Prix));
            if (lignes == 0)
                return new(AchatStatut.SoldeInsuffisant);   // pas de commit : la transaction est annulée

            _db.Purchases.Add(new Purchase
            {
                PlayerId = joueur.Id,
                Titre = jeu.Titre,
                PrixPaye = jeu.Prix,
                Date = DateTime.UtcNow
            });
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return new(AchatStatut.DejaPossede);        // l'index unique a refusé : le débit est annulé avec le reste
            }
            await _db.SaveChangesAsync();
        }
        var solde = await _db.Players.Where(p => p.Id == joueur.Id).Select(p => p.Coins).SingleAsync();

        // 3. Les projections : Neo4j, puis Redis
        await _graph.AddOwnershipAsync(pseudo, jeu.Titre, DateTime.UtcNow.Year);
        await _ventes.RecordSaleAsync(jeu.Titre);

        return new(AchatStatut.Ok, new AchatRecu(pseudo, jeu.Titre, jeu.Prix, solde, true));
    }
}