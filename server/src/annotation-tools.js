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

export function registerAnnotationTools(register) {
  register('list_annotation_types', 'Прочитать загруженные типы линейных размеров, марок стен и помещений. Возвращает существующие ID для инструментов аннотаций.', z.object({
    kind: z.enum(['all', 'linearDimensions', 'wallTags', 'roomTags']).default('all')
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
}
