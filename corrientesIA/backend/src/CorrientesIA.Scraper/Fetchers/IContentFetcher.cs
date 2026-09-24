using CorrientesIA.Scraper.Models;

namespace CorrientesIA.Scraper.Fetchers;

public interface IContentFetcher
{
    Task<ResultadoScrapeo?> FetchAsync(string url);
}
