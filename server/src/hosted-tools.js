import * as z from 'zod/v4';

export function registerHostedTools(register) {
  const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
  const meters = z.number().finite().min(-100000).max(100000);
  const instance = z.object({
    hostWallId: id,
    familySymbolId: id,
    levelId: id.optional(),
    levelName: z.string().trim().min(1).max(100).optional(),
    position: z.object({ xMeters: meters, yMeters: meters }).strict(),
    sillHeightMeters: z.number().finite().min(-100).max(100).optional(),
    offsetMeters: z.number().finite().min(-100).max(100).optional(),
    flipFacing: z.boolean().default(false),
    flipHand: z.boolean().default(false)
  }).strict().refine(args => (args.levelId !== undefined) !== (args.levelName !== undefined), {
    message: 'Укажите ровно одно поле: levelId или levelName.', path: ['levelId']
  }).refine(args => args.sillHeightMeters === undefined || args.offsetMeters === undefined, {
    message: 'sillHeightMeters и offsetMeters — альтернативные имена одного смещения; используйте одно.',
    path: ['sillHeightMeters']
  });

  register('preview_place_hosted_families',
    'Проверить размещение дверей и окон OneLevelBasedHosted на явно указанных прямых вертикальных базовых стенах. Выполняет реальное пробное создание в транзакции с полным откатом документа; возвращаемые временные ID после предпросмотра недействительны. Координаты X/Y в метрах на линии расположения стены, допуск 1 мм. sillHeightMeters (или offsetMeters) — высота подоконника/порога над выбранным уровнем, по умолчанию 0. flipFacing/flipHand переворачивают исходную ориентацию. Проверяет стандартные ширину и высоту. Не поддерживает связи, группы, редактированные профили, присоединённые верх/низ, криволинейные, наклонные и витражные стены; не выполняет проверку коллизий или формы нестандартных проёмов.',
    z.object({ instances: z.array(instance).min(1).max(50) }).strict(),
    { readOnlyHint: false, destructiveHint: false, openWorldHint: false });

  register('apply_place_hosted_families',
    'Показать подтверждение в Revit и разместить двери/окна из действующего плана preview_place_hosted_families. Весь пакет откатывается при ошибке. Используйте новый предпросмотр после изменения документа. При autoConfirm=true в локальных настройках окно подтверждения пропускается.',
    z.object({ planId: z.string().min(1).max(80) }).strict(),
    { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
}
