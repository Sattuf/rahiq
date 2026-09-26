using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Infrastructure.Common.Web;
using Rahiq.SharedKernel;
using Rahiq.SharedKernel.Compliance;

namespace Rahiq.Modules.Content
{
    public static class ContentModule
    {
        public static IServiceCollection AddContentModule(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddModuleAssembly(typeof(ContentModule).Assembly);
            return services;
        }
    }
}

namespace Rahiq.Modules.Content.Domain
{
    internal sealed partial class Page : Entity<Guid>
    {
        private Page()
        {
        }

        public string Slug { get; private set; } = string.Empty;

        public string Locale { get; private set; } = Locales.Default;

        public string Kind { get; private set; } = "story";

        public string Title { get; private set; } = string.Empty;

        public string? Summary { get; private set; }

        /// <summary>Markdown only. Rendered to sanitised HTML by the storefront, never raw HTML from the admin (security.md §7).</summary>
        public string BodyMarkdown { get; private set; } = string.Empty;

        public string Status { get; private set; } = "draft";

        public List<Guid> RelatedProductIds { get; private set; } = [];

        public DateTimeOffset UpdatedAt { get; private set; }

        public DateTimeOffset? PublishedAt { get; private set; }

        public static Result<Page> Create(string slug, string locale, string kind, DateTimeOffset now)
        {
            if (!SlugPattern().IsMatch(slug) || !Locales.IsSupported(locale) || kind is not ("legal" or "story" or "guide" or "faq"))
            {
                return Error.Validation("page.invalid", "Slug is lowercase latin with dashes; locale tr/ar/en; kind legal/story/guide/faq.");
            }

            return new Page { Id = Ids.New(), Slug = slug, Locale = locale, Kind = kind, UpdatedAt = now };
        }

        public void Edit(string title, string? summary, string body, IReadOnlyList<Guid> related, DateTimeOffset now)
        {
            Title = title.Trim();
            Summary = summary?.Trim();
            BodyMarkdown = body;
            RelatedProductIds = [.. related];
            UpdatedAt = now;
        }

        public void Publish(DateTimeOffset now)
        {
            Status = "published";
            PublishedAt ??= now;
            UpdatedAt = now;
        }

        public void Unpublish(DateTimeOffset now)
        {
            Status = "draft";
            UpdatedAt = now;
        }

        [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        private static partial Regex SlugPattern();
    }
}

namespace Rahiq.Modules.Content.Infrastructure
{
    using Rahiq.Modules.Content.Domain;

    internal sealed class ContentModel : IModelContributor
    {
        public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<Page>(b =>
        {
            b.ToTable("pages", "content");
            b.HasKey(p => p.Id);
        });
    }
}

namespace Rahiq.Modules.Content.Application
{
    using Rahiq.Modules.Content.Domain;

    public sealed record PageDto(Guid Id, string Slug, string Locale, string Kind, string Title, string? Summary, string BodyMarkdown, string Status, IReadOnlyList<Guid> RelatedProductIds, DateTimeOffset UpdatedAt);

    public sealed record PageInput(string Slug, string Locale, string Kind, string Title, string? Summary, string BodyMarkdown, IReadOnlyList<Guid>? RelatedProductIds);

    public sealed record PageSaved(Guid Id, IReadOnlyList<ClaimFinding> Findings);

    public sealed record GetPageQuery(string Slug, string Locale) : IQuery<PageDto?>;

    public sealed record ListPagesQuery(string? Kind, string? Locale, bool IncludeDrafts) : IQuery<IReadOnlyList<PageDto>>;

    public sealed record SavePageCommand(Guid? Id, PageInput Page) : ICommand<Result<PageSaved>>;

    /// <param name="ApproveClaims">Same rule as products: forbidden terms block publishing unless a manager approves.</param>
    public sealed record PublishPageCommand(Guid Id, bool Publish, bool ApproveClaims) : ICommand<Result>;

    internal sealed class ContentHandlers(RahiqDbContext db, ClaimsGuard guard, ICurrentActor actor, IAuditLog audit, IClock clock)
        : IRequestHandler<GetPageQuery, PageDto?>,
          IRequestHandler<ListPagesQuery, IReadOnlyList<PageDto>>,
          IRequestHandler<SavePageCommand, Result<PageSaved>>,
          IRequestHandler<PublishPageCommand, Result>
    {
        public async Task<PageDto?> Handle(GetPageQuery request, CancellationToken cancellationToken)
        {
            var page = await db.Set<Page>().AsNoTracking().FirstOrDefaultAsync(p => p.Slug == request.Slug && p.Locale == request.Locale && p.Status == "published", cancellationToken)
                ?? await db.Set<Page>().AsNoTracking().FirstOrDefaultAsync(p => p.Slug == request.Slug && p.Locale == Locales.Default && p.Status == "published", cancellationToken);
            return page is null ? null : ToDto(page);
        }

        public async Task<IReadOnlyList<PageDto>> Handle(ListPagesQuery request, CancellationToken cancellationToken)
        {
            var query = db.Set<Page>().AsNoTracking().AsQueryable();
            if (!request.IncludeDrafts)
            {
                query = query.Where(p => p.Status == "published");
            }

            if (request.Kind is not null)
            {
                query = query.Where(p => p.Kind == request.Kind);
            }

            if (request.Locale is not null)
            {
                query = query.Where(p => p.Locale == request.Locale);
            }

            return [.. (await query.OrderBy(p => p.Kind).ThenBy(p => p.Slug).ToListAsync(cancellationToken)).Select(ToDto)];
        }

        public async Task<Result<PageSaved>> Handle(SavePageCommand request, CancellationToken cancellationToken)
        {
            var input = request.Page;
            if (string.IsNullOrWhiteSpace(input.Title) || input.BodyMarkdown.Length > 100_000)
            {
                return Error.Validation("page.invalid", "A page needs a title and at most 100,000 characters.");
            }

            Page? page;
            if (request.Id is { } id)
            {
                page = await db.Set<Page>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
                if (page is null)
                {
                    return Error.NotFound("page.not_found", "Page not found.");
                }
            }
            else
            {
                if (await db.Set<Page>().AnyAsync(p => p.Slug == input.Slug && p.Locale == input.Locale, cancellationToken))
                {
                    return Error.Conflict("page.exists", "A page with this slug exists in this language.");
                }

                var created = Page.Create(input.Slug, input.Locale, input.Kind, clock.UtcNow);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                page = created.Value;
                db.Add(page);
            }

            page.Edit(input.Title, input.Summary, input.BodyMarkdown, input.RelatedProductIds ?? [], clock.UtcNow);
            var findings = Check(page);
            if (page.Status == "published" && findings.Any(f => f.Severity == FindingSeverity.Blocking))
            {
                page.Unpublish(clock.UtcNow);
            }

            return new PageSaved(page.Id, findings);
        }

        public async Task<Result> Handle(PublishPageCommand request, CancellationToken cancellationToken)
        {
            var page = await db.Set<Page>().FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
            if (page is null)
            {
                return Error.NotFound("page.not_found", "Page not found.");
            }

            if (!request.Publish)
            {
                page.Unpublish(clock.UtcNow);
                return Result.Success();
            }

            var blocking = Check(page).Where(f => f.Severity == FindingSeverity.Blocking).ToList();
            if (blocking.Count > 0)
            {
                if (!request.ApproveClaims || !actor.HasPermission(Permissions.ClaimsOverride))
                {
                    return Error.Conflict("page.forbidden_claims", "The text contains forbidden health claims.") with
                    {
                        Details = blocking.GroupBy(f => f.Field).ToDictionary(g => g.Key, g => g.Select(f => f.Term).ToArray()),
                    };
                }

                audit.Record("claims.override", "page", page.Id.ToString(), blocking.Select(b => b.Term));
            }

            page.Publish(clock.UtcNow);
            audit.Record("page.published", "page", page.Id.ToString());
            return Result.Success();
        }

        private IReadOnlyList<ClaimFinding> Check(Page page) =>
            guard.Check([new("title", page.Title), new("summary", page.Summary), new("body", page.BodyMarkdown)]);

        private static PageDto ToDto(Page p) => new(p.Id, p.Slug, p.Locale, p.Kind, p.Title, p.Summary, p.BodyMarkdown, p.Status, p.RelatedProductIds, p.UpdatedAt);
    }
}

namespace Rahiq.Modules.Content.Presentation
{
    using Rahiq.Modules.Content.Application;

    [Route("api/content")]
    public sealed class ContentController(ISender sender) : ApiControllerBase
    {
        [HttpGet("pages")]
        public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken ct) => Ok(await sender.Send(new ListPagesQuery(kind, Locale, false), ct));

        [HttpGet("pages/{slug}")]
        public async Task<IActionResult> Get(string slug, CancellationToken ct)
        {
            var page = await sender.Send(new GetPageQuery(slug, Locale), ct);
            return page is null ? NotFound() : Ok(page);
        }
    }

    public sealed record PublishPageRequest(bool Publish, bool ApproveClaims);

    [Route("api/admin/content")]
    [Authorize(Policy = Permissions.ContentEdit)]
    public sealed class AdminContentController(ISender sender) : ApiControllerBase
    {
        [HttpGet("pages")]
        public async Task<IActionResult> List([FromQuery] string? kind, [FromQuery] string? locale, CancellationToken ct) =>
            Ok(await sender.Send(new ListPagesQuery(kind, locale, true), ct));

        [HttpPost("pages")]
        public async Task<IActionResult> Create(PageInput body, CancellationToken ct) => FromResult(await sender.Send(new SavePageCommand(null, body), ct));

        [HttpPut("pages/{id:guid}")]
        public async Task<IActionResult> Update(Guid id, PageInput body, CancellationToken ct) => FromResult(await sender.Send(new SavePageCommand(id, body), ct));

        [HttpPost("pages/{id:guid}/publish")]
        public async Task<IActionResult> Publish(Guid id, PublishPageRequest body, CancellationToken ct) =>
            FromResult(await sender.Send(new PublishPageCommand(id, body.Publish, body.ApproveClaims), ct));
    }
}
