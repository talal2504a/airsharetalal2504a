/*
 AirShare — database schema

 MonsterASP panel ke SQL editor me PURA paste karo aur run karo.
 Script idempotent hai — dobara chalane se kuch nahi tootega.
*/

IF OBJECT_ID('dbo.Shares', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Shares
    (
        Id            INT IDENTITY(1,1) NOT NULL,
        NetworkIp     VARCHAR(45)    NOT NULL,
        ShareType     VARCHAR(10)    NOT NULL,
        FileName      NVARCHAR(255)  NULL,
        StoredName    VARCHAR(100)   NULL,
        TextContent   NVARCHAR(MAX)  NULL,
        FileSize      BIGINT         NULL,
        Code          VARCHAR(6)     NULL,
        CreatedAt     DATETIME2(3)   NOT NULL
            CONSTRAINT DF_Shares_CreatedAt DEFAULT SYSUTCDATETIME(),
        ExpiresAt     DATETIME2(3)   NOT NULL,
        DownloadCount INT            NOT NULL
            CONSTRAINT DF_Shares_DownloadCount DEFAULT 0,

        CONSTRAINT PK_Shares PRIMARY KEY CLUSTERED (Id),

        -- Sirf 'file' ya 'text' — doosra kuch nahi
        CONSTRAINT CK_Shares_ShareType CHECK (ShareType IN ('file', 'text')),

        -- Code ya NULL hoga, aur agar ho to sirf digits
        CONSTRAINT CK_Shares_Code CHECK (Code IS NULL OR Code NOT LIKE '%[^0-9]%')
    );

    PRINT 'Table dbo.Shares created.';
END
ELSE
BEGIN
    PRINT 'Table dbo.Shares already exists — skipped.';
END
GO

-- ---------------------------------------------------------------------
-- Index 1: main listing query
--   WHERE NetworkIp = ? AND ExpiresAt > ?
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Shares_NetworkIp_ExpiresAt'
      AND object_id = OBJECT_ID('dbo.Shares'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Shares_NetworkIp_ExpiresAt
        ON dbo.Shares (NetworkIp, ExpiresAt)
        INCLUDE (ShareType, FileName, FileSize, Code, CreatedAt, DownloadCount);

    PRINT 'Index IX_Shares_NetworkIp_ExpiresAt created.';
END
GO

-- ---------------------------------------------------------------------
-- Index 2: cleanup query
--   WHERE ExpiresAt <= ?
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Shares_ExpiresAt'
      AND object_id = OBJECT_ID('dbo.Shares'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Shares_ExpiresAt
        ON dbo.Shares (ExpiresAt);

    PRINT 'Index IX_Shares_ExpiresAt created.';
END
GO

-- ---------------------------------------------------------------------
-- Index 3: code lookup (cross-network download) — UNIQUE + FILTERED
--   Filtered isliye ki NULL codes ek dusre ko block na karein
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE name = 'UX_Shares_Code'
      AND object_id = OBJECT_ID('dbo.Shares'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Shares_Code
        ON dbo.Shares (Code)
        WHERE Code IS NOT NULL;

    PRINT 'Unique index UX_Shares_Code created.';
END
GO