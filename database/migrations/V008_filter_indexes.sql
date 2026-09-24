-- fakt:id=V008
-- fakt:title=Индексы фильтров поиска (необязательно)
-- fakt:description=Добавляет некластеризованные индексы для фильтров «Фамилия/Имя» и «Дата рождения» в PersonFacts. Ускоряет поиск по началу фамилии и диапазону дат на больших объёмах; увеличивает размер базы и время записи. Данные не изменяются.
-- fakt:alters-user-tables=true
-- fakt:required=
-- fakt:transaction=true

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{PF_LIT}}) AND name = N'IX_{{PF_NAME_LIT}}_Name')
    CREATE NONCLUSTERED INDEX [IX_{{PF_NAME}}_Name] ON {{PF}} ({{c:Surname}}, {{c:Name}}) INCLUDE ({{c:Patronymic}}, {{c:BirthDate}});
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{PF_LIT}}) AND name = N'IX_{{PF_NAME_LIT}}_BirthDate')
    CREATE NONCLUSTERED INDEX [IX_{{PF_NAME}}_BirthDate] ON {{PF}} ({{c:BirthDate}});
GO
