-- fakt:id=V004
-- fakt:title=Проверка ISJSON и значение по умолчанию для [ALL]
-- fakt:description=Добавляет ограничение ISJSON([ALL]) = 1 и значение по умолчанию N'{}', если их нет. Ограничение создаётся с проверкой существующих строк: если в таблице уже есть некорректный JSON, миграция откатывается и сообщает число таких строк — данные не изменяются.
-- fakt:alters-user-tables=true
-- fakt:required=
-- fakt:transaction=true

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID({{PF_LIT}})
      AND LOWER(definition) LIKE N'%isjson%')
BEGIN
    DECLARE @invalid BIGINT = (SELECT COUNT_BIG(*) FROM {{PF}} WHERE ISJSON({{c:All}}) <> 1 OR {{c:All}} IS NULL);
    IF @invalid > 0
    BEGIN
        DECLARE @message NVARCHAR(400) = CONCAT(N'В таблице найдено строк с некорректным JSON в [ALL]: ', @invalid, N'. Ограничение не добавлено; исправьте данные и повторите миграцию.');
        THROW 51004, @message, 1;
    END

    ALTER TABLE {{PF}} WITH CHECK ADD CONSTRAINT [CK_{{PF_NAME}}_ALL_IsJson] CHECK (ISJSON({{c:All}}) = 1);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID({{PF_LIT}})
      AND parent_column_id = COLUMNPROPERTY(OBJECT_ID({{PF_LIT}}), {{c_lit:All}}, 'ColumnId'))
    ALTER TABLE {{PF}} ADD CONSTRAINT [DF_{{PF_NAME}}_ALL] DEFAULT (N'{}') FOR {{c:All}};
GO
