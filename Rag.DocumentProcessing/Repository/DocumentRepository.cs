using Rag.DocumentProcessing.Models;

namespace Rag.DocumentProcessing.Data;

public static class DocumentRepository
{
    public static readonly List<DocumentInfo> Documents =
    [
        new()
        {
            DocumentId = 1,
            ProjectId = 1,
            DocumentName = "AMS Requirements v1.0",
            DocumentType = "Requirements",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Requirements/AMS-Requirements-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 2,
            ProjectId = 1,
            DocumentName = "AMS Architecture v1.0",
            DocumentType = "Architecture",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Architecture/AMS-Architecture-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 3,
            ProjectId = 1,
            DocumentName = "AMS API Documentation v1.0",
            DocumentType = "API Documentation",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/API-Documentation/AMS-API-Documentation-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 4,
            ProjectId = 1,
            DocumentName = "AMS Database Design v1.0",
            DocumentType = "Database Design",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Database-Design/AMS-Database-Design-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 7,
            ProjectId = 1,
            DocumentName = "AMS SOP Asset Issuance",
            DocumentType = "SOP",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/SOP/AMS-SOP-Asset-Issuance-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 8,
            ProjectId = 1,
            DocumentName = "AMS SOW",
            DocumentType = "SOW",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/SOW/AMS-SOW-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 9,
            ProjectId = 1,
            DocumentName = "AMS Kickoff Meeting Notes",
            DocumentType = "Meeting Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/AMS-Kickoff-Meeting-Notes-2026-08-05.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 10,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v1.0",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 11,
            ProjectId = 1,
            DocumentName = "AMS User Guide v1.0",
            DocumentType = "User Guide",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/User-Guide/AMS-User-Guide-v1.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 12,
            ProjectId = 1,
            DocumentName = "AMS BUG 001",
            DocumentType = "Bug Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Bug-Tickets/AMS-BUG-001.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 13,
            ProjectId = 1,
            DocumentName = "AMS ENH 001",
            DocumentType = "Enhancement Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Enhancement-Tickets/AMS-ENH-001.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 14,
            ProjectId = 1,
            DocumentName = "AMS SUP 001",
            DocumentType = "Support Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Support-Tickets/AMS-SUP-001.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 15,
            ProjectId = 1,
            DocumentName = "AMS Deployment",
            DocumentType = "Deployment Guide",
            FileExtension = ".txt",
            RelativePath = "Asset-Management-System/Deployment/AMS-Deployment-Operations-Guide-v1.0.txt",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 16,
            ProjectId = 1,
            DocumentName = "AMS Management and Guide",
            DocumentType = "Incident Management & Support Guide",
            FileExtension = ".txt",
            RelativePath = "Asset-Management-System/Incident Management & Support Guide/AMS Incident Management & Support Guide.txt",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 17,
            ProjectId = 1,
            DocumentName = "AMS Production Operations & Change",
            DocumentType = "Production Operations & Change",
            FileExtension = ".txt",
            RelativePath = "Asset-Management-System/AMS Production Operations & Change Management Guide v2.0/AMS Production Operations & Change Management Guide.txt",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 18,
            ProjectId = 1,
            DocumentName = "AMS Data Dictionary",
            DocumentType = "Data Dictionary",
            FileExtension = ".xlxs",
            RelativePath = "Asset-Management-System/Data-Dictionary/AMS-Data-Dictionary-v1.0.xlsx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 19,
            ProjectId = 1,
            DocumentName = "AMS Preventive Maintenance Requirements",
            DocumentType = "Preventive Maintenance Requirements",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/Asset Maintenance Management-2026-08-30.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 20,
            ProjectId = 1,
            DocumentName = "AMS Asset Registration and Asset Information Management",
            DocumentType = "Asset Registration and Asset Information Management",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/Asset Registration and Asset Information Management-2026-08-17.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 21,
            ProjectId = 1,
            DocumentName = "AMS Asset Registration Requirements",
            DocumentType = "Asset Registration Requirements",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/Asset Registration Requirements-2026-08-15.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 22,
            ProjectId = 1,
            DocumentName = "AMS Maintenance Cost and Spare Parts Management",
            DocumentType = "Maintenance Cost and Spare Parts Management",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/Maintenance Cost and Spare Parts Management-2026-09-10.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 23,
            ProjectId = 1,
            DocumentName = "AMS Preventive Maintenance Requirements",
            DocumentType = "Preventive Maintenance Requirements",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Meeting-Notes/Preventive Maintenance Requirements-2026-09-03.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 24,
            ProjectId = 1,
            DocumentName = "AMS BUG 002",
            DocumentType = "Bug Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Bug-Tickets/AMS-BUG-002.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 25,
            ProjectId = 1,
            DocumentName = "AMS BUG 003",
            DocumentType = "Bug Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Bug-Tickets/AMS-BUG-003.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 26,
            ProjectId = 1,
            DocumentName = "AMS BUG 004",
            DocumentType = "Bug Ticket",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Bug-Tickets/AMS-BUG-004.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 27,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v1.1",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v1.1.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 28,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v1.2",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v1.2.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 29,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v1.3",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v1.3.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 30,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v1.4",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v1.4.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 31,
            ProjectId = 1,
            DocumentName = "AMS Release Notes v2.0",
            DocumentType = "Release Notes",
            FileExtension = ".docx",
            RelativePath = "Asset-Management-System/Release-Notes/AMS-Release-Notes-v2.0.docx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 32,
            ProjectId = 1,
            DocumentName = "AMS Database Schema",
            DocumentType = "Database Schema",
            FileExtension = ".sql",
            RelativePath = "Asset-Management-System/SQL-Scripts/AMS-Database-Schema-v1.0.sql",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 33,
            ProjectId = 1,
            DocumentName = "AMS Database Schema - 1",
            DocumentType = "Database Schema - 1",
            FileExtension = ".sql",
            RelativePath = "Asset-Management-System/SQL-Scripts/DropVendorTableSqlSrcipt.sql",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 34,
            ProjectId = 1,
            DocumentName = "Team - Dot net Developer",
            DocumentType = "Monthly KPI Review",
            FileExtension = ".xlsx",
            RelativePath = "Asset-Management-System/KPI/Team - Dot net Developer.xlsx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 35,
            ProjectId = 1,
            DocumentName = "asset management data",
            DocumentType = "Asset Management Data",
            FileExtension = ".xlsx",
            RelativePath = "Asset-Management-System/Asset Management Data/asset_management_data.xlsx",
            CreatedOn = new DateTime(2026, 8, 5)
        },
        new()
        {
            DocumentId = 36,
            ProjectId = 1,
            DocumentName = "Project Management Data",
            DocumentType = "Project Management Data",
            FileExtension = ".xlsx",
            RelativePath = "Asset-Management-System/Project Management Data/Project-Management-Data.xlsx",
            CreatedOn = new DateTime(2026, 8, 5)
        }
    ];
}