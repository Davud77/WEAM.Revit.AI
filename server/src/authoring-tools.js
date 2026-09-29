import * as z from 'zod/v4';
const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const plan = z.object({ planId: z.string().min(1).max(80) }).strict();
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };
const point = z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }).strict();

export function registerAuthoringTools(register) {
  register('export_view_image', 'Экспортировать существующий печатаемый вид Revit в PNG для визуальной проверки. Модель не меняется; создаётся новая подпапка в указанной локальной директории. Возвращает пути PNG.', z.object({ viewId: id, directory: z.string().min(1).max(170), pixelSize: z.number().int().min(500).max(4000).default(2000) }).strict(), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
  register('preview_save_project', 'Подготовить сохранение открытого отдельного проекта в его текущий RVT. Возвращает путь; запись файла происходит только в apply.', z.object({}).strict());
  register('apply_save_project', 'Сохранить изменения текущего отдельного проекта по проверенному плану. При autoConfirm=true выполняется без окна подтверждения.', plan, write);
  register('preview_duplicate_family_types', 'Проверить копирование существующих типов дверей/окон с новыми именами и точными стандартными шириной/высотой в метрах. Исходные типы не меняются. Реальное пробное создание с откатом.', z.object({ types: z.array(z.object({ sourceSymbolId: id, name: z.string().trim().min(1).max(200), widthMeters: z.number().finite().min(0.1).max(10), heightMeters: z.number().finite().min(0.1).max(10) }).strict()).min(1).max(20) }).strict(), write);
  register('apply_duplicate_family_types', 'Создать проверенные типоразмеры после подтверждения в Revit. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', plan, write);
  register('preview_load_family', 'Проверить загрузку локального файла .rfa с откатом. Полный локальный путь обязателен. Уже загруженные семейства не перезаписываются; файл проверяется по SHA-256 при apply.', z.object({ path: z.string().min(1).max(240) }).strict(), write);
  register('apply_load_family', 'Загрузить проверенное семейство после подтверждения в Revit. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', plan, write);
  register('preview_create_room_separators', 'Проверить прямые линии разделения помещений на плане этажа. Координаты XY в метрах от внутреннего начала. Реальное создание и откат; линии сохранятся только после apply.', z.object({ viewId: id.optional(), lines: z.array(z.object({ start: point, end: point }).strict()).min(1).max(100) }).strict(), write);
  register('apply_create_room_separators', 'Создать проверенные линии разделения помещений после подтверждения в Revit. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', plan, write);
  register('preview_save_project_as', 'Подготовить сохранение текущего отдельного проекта под новым локальным именем .rvt. Существующие файлы не перезаписываются; файлы не записываются на этапе preview.', z.object({ path: z.string().min(1).max(240) }).strict());
  register('apply_save_project_as', 'Сохранить проект как новый .rvt после подтверждения в Revit. Активный документ перейдёт на новый путь. Не поддерживает совместную работу. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', plan, write);
}
