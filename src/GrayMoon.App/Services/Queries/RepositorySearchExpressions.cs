using System.Linq.Expressions;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services.Queries;

internal static class RepositorySearchExpressions
{
    public static Expression<Func<Repository, bool>> BuildTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            if (term.Field == "topic")
            {
                var value = term.Value.ToLowerInvariant();
                return r => (r.Topics ?? string.Empty).ToLower().Contains(value);
            }

            return _ => true;
        }

        var termValue = term.Value.ToLowerInvariant();
        return r => r.RepositoryName.ToLower().Contains(termValue)
                    || (r.OrgName ?? string.Empty).ToLower().Contains(termValue)
                    || (r.Topics ?? string.Empty).ToLower().Contains(termValue)
                    || (r.Connector != null ? r.Connector.ConnectorName : "Unknown").ToLower().Contains(termValue);
    }
}
