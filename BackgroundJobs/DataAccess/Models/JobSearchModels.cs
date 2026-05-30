// DataAccess/Models/JobSearchModels.cs
// DTOs for the portal job-search endpoint and reference-data dropdowns.
namespace CareerPanda.DataAccess.Models;

// ── Reference data ────────────────────────────────────────────────────────────

public class IndustryDto
{
    public int    Id   { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class JobRoleDto
{
    public int    Id         { get; set; }
    public int?   IndustryId { get; set; }
    public string Slug       { get; set; } = null!;
    public string Name       { get; set; } = null!;
    public string? SearchQuery { get; set; }
}

// ── Search query ─────────────────────────────────────────────────────────────

public class RawJobSearchQuery
{
    public string?   Keyword       { get; set; }   // full-text search on title + description
    public int[]?    IndustryIds   { get; set; }   // multi-select
    public int[]?    JobRoleIds    { get; set; }   // multi-select
    public string[]? WorkTypes     { get; set; }   // Remote|Hybrid|OnSite
    public string[]? ContractTypes { get; set; }   // FullTime|PartTime|Contract|Internship
    public string[]? States        { get; set; }   // two-letter US state codes
    public string?   JobLevel      { get; set; }   // Entry|Mid|Senior|Lead|Director
    public int?      SalaryMin     { get; set; }
    public int?      PostedWithinDays { get; set; } // 1|3|7|30|90
    public bool?     H1BSponsored  { get; set; }
    public bool?     OptCpt        { get; set; }
    public bool?     GreenCard     { get; set; }
    public bool?     Remote        { get; set; }   // convenience shortcut → WorkType=Remote
    public int       Page          { get; set; } = 1;
    public int       PageSize      { get; set; } = 20;
}

// ── Search result ─────────────────────────────────────────────────────────────

public class RawJobSearchResult
{
    public int      Id            { get; set; }
    public string   PublicId      { get; set; } = null!;
    public string   JobTitle      { get; set; } = null!;
    public string?  JobLink       { get; set; }
    public string   CompanyName   { get; set; } = null!;
    public string?  CompanyLogo   { get; set; }
    public string?  CompanyWebsite{ get; set; }
    public string?  Industry      { get; set; }   // md.industries.name
    public string?  Role          { get; set; }   // md.job_roles.name
    public string?  WorkType      { get; set; }   // Remote|Hybrid|OnSite
    public string?  ContractType  { get; set; }   // FullTime|PartTime|Contract|Internship
    public string?  JobLevel      { get; set; }
    public string?  City          { get; set; }
    public string?  State         { get; set; }
    public decimal? SalaryMin     { get; set; }
    public decimal? SalaryMax     { get; set; }
    public string?  SalaryType    { get; set; }
    public DateTime? PostDate     { get; set; }
    public bool?    IsH1BSponsored { get; set; }
    public bool?    IsOptCpt      { get; set; }
    public bool?    IsGreenCard   { get; set; }
    public string?  Source        { get; set; }
}
