-- fakt:id=V007
-- fakt:title=Полнотекстовый каталог и индекс
-- fakt:description=Создаёт полнотекстовый каталог FaktCatalog и индекс по поисковой проекции (AllText, NameText, BirthPlaceText, FactsText, FileText) с русским средством разбиения слов (LCID 1049), без стоп-слов и с автоматическим отслеживанием изменений. Индекс обновляется асинхронно: только что сохранённые записи появляются в полнотекстовом поиске с задержкой. Требуется установленный компонент Full-Text Search. Выполняется вне транзакции (ограничение SQL Server).
-- fakt:alters-user-tables=false
-- fakt:required=
-- fakt:transaction=false

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') <> 1
    THROW 51007, N'Компонент Full-Text Search не установлен на сервере SQL Server. Установите его (компонент «Full-Text and Semantic Extractions for Search») и повторите миграцию.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'FaktCatalog')
    CREATE FULLTEXT CATALOG [FaktCatalog] WITH ACCENT_SENSITIVITY = OFF;
GO

IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktSearchDocs}}))
    CREATE FULLTEXT INDEX ON {{AUX:FaktSearchDocs}}
    (
        [AllText] LANGUAGE 1049,
        [NameText] LANGUAGE 1049,
        [BirthPlaceText] LANGUAGE 1049,
        [FactsText] LANGUAGE 1049,
        [FileText] LANGUAGE 1049
    )
    KEY INDEX [PK_FaktSearchDocs]
    ON [FaktCatalog]
    WITH (CHANGE_TRACKING = AUTO, STOPLIST = OFF);
GO
