import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const point = z.object({
  xMeters: z.number().finite().min(-100000).max(100000),
  yMeters: z.number().finite().min(-100000).max(100000),
  zMeters: z.number().finite().min(-100000).max(100000)
}).strict();
const apply = z.object({ planId: z.string().min(1).max(80) }).strict();
const writeHints = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const tagSchema = (defaultUp) => z.object({
  viewId: id.optional(),
  tagTypeId: id,
  elementIds: z.array(id).min(1).max(50).optional(),
  leader: z.boolean().default(false),
  offsetRightPaperMillimeters: z.number().finite().min(-100).max(100).default(0),
  offsetUpPaperMillimeters: z.number().finite().min(-100).max(100).default(defaultUp)
}).strict();
const mepCategories = ['OST_DuctCurves','OST_FlexDuctCurves','OST_DuctFitting','OST_DuctAccessory',
  'OST_DuctTerminal','OST_MechanicalEquipment','OST_DuctInsulations','OST_PipeCurves',
  'OST_FlexPipeCurves','OST_PipeFitting','OST_PipeAccessory','OST_PipeInsulations'];
const mepTag = z.object({
  elementId: id, tagTypeId: id.optional(), headPosition: point.optional(),
  leader: z.boolean().default(true), leaderEndPosition: point.optional(), elbowPosition: point.optional(),
  orientation: z.enum(['horizontal','vertical']).default('horizontal'),
  offsetRightPaperMillimeters: z.number().finite().min(-100).max(100).default(10),
  offsetUpPaperMillimeters: z.number().finite().min(-100).max(100).default(5)
}).strict().refine(value => value.leader || (!value.leaderEndPosition && !value.elbowPosition),
  { message: 'Leader points require leader=true' })
  .refine(value => !value.headPosition || (value.offsetRightPaperMillimeters === 10 && value.offsetUpPaperMillimeters === 5),
  { message: 'Explicit headPosition cannot be combined with custom offsets' });

export function registerAnnotationTools(register) {
  register('list_annotation_types', 'Прочитать загруженные типы линейных размеров, марок стен, помещений и MEP. Для kind=mepTags возвращает фактическую категорию модели и категорию марки: используйте их для categoryTypes. Возвращает существующие ID текущего документа.', z.object({
    kind: z.enum(['all', 'linearDimensions', 'wallTags', 'roomTags', 'mepTags']).default('all')
  }).strict());
  register('get_element_references', 'Найти стабильные ссылки на плоские грани и опорные плоскости/линии семейств в текущем документе. Возвращает elementId, stableReference, координаты и нормали граней и плоскость выбранного вида. Ссылки — кандидаты; совместимость проверяется preview_create_dimensions. Связанные модели не поддерживаются.', z.object({
    elementIds: z.array(id).min(1).max(20),
    viewId: id.optional(),
    maxReferencesPerElement: z.number().int().min(1).max(200).default(100)
  }).strict());
  register('preview_create_dimensions', 'Проверить до 20 линейных размерных цепочек реальным созданием и откатом транзакции Revit. Требуются явные ссылки из get_element_references, существующий линейный DimensionType и линия в плоскости вида в метрах от внутреннего начала. Возвращает измерения сегментов; временные ID недействительны после отката. Поддерживаются планы, разрезы, фасады, узлы и чертёжные виды. Угловые/дуговые размеры и связанные модели не поддерживаются.', z.object({
    viewId: id.optional(),
    dimensions: z.array(z.object({
      dimensionTypeId: id,
      line: z.object({ start: point, end: point }).strict(),
      references: z.array(z.object({ elementId: id, stableReference: z.string().min(1).max(4096) }).strict()).min(2).max(20)
    }).strict()).min(1).max(20)
  }).strict());
  register('apply_create_dimensions', 'Запросить подтверждение в Revit и создать линейные размеры по действующему плану предпросмотра. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', apply, writeHints);
  register('preview_tag_walls', 'Проверить размещение марок у середины стен на плане этажа или потолка реальным созданием и откатом. Тип марки должен быть загружен. Без elementIds выбираются стены вида; более 50 требуют явного пакета ID. Уже промаркированные и невидимые стены пропускаются. Смещения головы марки задаются в миллиметрах на бумаге относительно осей вида.', tagSchema(5));
  register('apply_tag_walls', 'Запросить подтверждение в Revit и разместить марки стен по действующему плану предпросмотра. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', apply, writeHints);
  register('preview_tag_rooms', 'Проверить марки помещений на плане этажа или потолка реальным созданием и откатом. Требуется загруженный тип. Без elementIds выбираются помещения вида; максимум 50. Уже промаркированные, невидимые, неразмещённые и незамкнутые помещения пропускаются. Смещения задаются в миллиметрах на бумаге относительно осей вида.', tagSchema(0));
  register('apply_tag_rooms', 'Запросить подтверждение в Revit и разместить марки помещений по действующему плану предпросмотра. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', apply, writeHints);
  register('preview_tag_mep', 'Проверить пакет до 50 нативных MEP-марок созданием и откатом. Воздуховоды, трубы, гибкие участки, фитинги, арматура, воздухораспределители, оборудование, изоляция. Планы, разрезы, фасады, узлы и заблокированный ортографический 3D. Тип указывается явно или выбирается по фактической категории через categoryTypes; используйте list_annotation_types(kind=mepTags). Дубли того же типа и невидимые элементы пропускаются. Пустая подпись отменяет пакет. Координаты в метрах от внутреннего начала; смещения головы в мм на бумаге по осям вида. Выноска прикреплена; явный конец делает её свободной. Связанные модели и автоматическое устранение наложений не поддерживаются.', z.object({
    viewId: id.optional(), tags: z.array(mepTag).min(1).max(50)
      .refine(items => new Set(items.map(item => item.elementId)).size === items.length,{message:'Duplicate elementIds'}),
    categoryTypes: z.array(z.object({ category: z.enum(mepCategories), tagTypeId: id }).strict()).min(1).max(12)
      .refine(items => new Set(items.map(item => item.category)).size === items.length,{message:'Duplicate category mappings'}).optional()
  }).strict().refine(value => value.categoryTypes?.length || value.tags.every(item => item.tagTypeId),
    {message:'Supply tagTypeId for each item or categoryTypes'}));
  register('apply_tag_mep', 'Создать инженерные марки по действующему предпросмотру. Повторная проверка документа, типов, опор и дублей. При autoConfirm=true применяется без окна подтверждения.', apply, writeHints);
}
