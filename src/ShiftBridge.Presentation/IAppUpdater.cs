using ShiftBridge.Core.Updates;

namespace ShiftBridge.Presentation;

/// <summary>Recherche une version plus récente de l'application hôte.</summary>
public interface IAppUpdateChecker
{
    /// <summary>La version publiée si elle est plus récente que celle qui tourne ; null sinon. Lève une exception si le réseau échoue.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct);
}

/// <summary>Mise à jour de l'application hôte (Windows : exe unique remplacé en place).</summary>
public interface IAppUpdater : IAppUpdateChecker
{
    /// <summary>Vrai si l'application peut se remplacer elle-même (dossier inscriptible, empreinte fournie) ; sinon on ouvre la page de la release.</summary>
    bool CanSelfUpdate(UpdateInfo update);

    /// <summary>Télécharge et vérifie la nouvelle version ; renvoie le chemin du fichier vérifié. Lève une exception si l'empreinte ne correspond pas.</summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct);

    /// <summary>Remplace l'exécutable par le fichier téléchargé, lance la nouvelle version et ferme l'application.</summary>
    void ApplyAndRestart(string downloadedPath);
}
