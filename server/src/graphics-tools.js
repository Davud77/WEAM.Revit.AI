import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const rgb = z.object({
  r: z.number().int().min(0).max(255),
  g: z.number().int().min(0).max(255),
  b: z.number().int().min(0).max(255)
}).strict();
const plan = z.object({ planId: z.string().min(1).max(80) }).strict();
const writeHints = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };

export function registerGraphicsTools(register) {
  register('preview_element_graphics',
    'Проверить графику и видимость явного списка элементов на указанном виде, выполнить пробную транзакцию и откатить её. Доступны цвет (линии и сплошная заливка), прозрачность 0–100, полный сброс переопределений, скрытие/показ и временное скрытие/изоляция. temporary_isolate заменяет текущую временную изоляцию; reset_temporary сбрасывает её на всём виде. unhide отменяет только индивидуальное постоянное скрытие. Для временной обработки групп укажите IDs их членов.',
    z.object({
      viewId: id,
      elementIds: z.array(id).min(1).max(1000).optional(),
      action: z.enum(['set_color', 'set_transparency', 'reset_overrides', 'hide', 'unhide', 'temporary_hide', 'temporary_isolate', 'reset_temporary']),
      color: rgb.optional(),
      transparency: z.number().int().min(0).max(100).optional()
    }).strict().superRefine((args, context) => {
      const issue = (path, message) => context.addIssue({ code: 'custom', path: [path], message });
      if (args.action === 'reset_temporary' && args.elementIds) issue('elementIds', 'Для reset_temporary не передавайте elementIds.');
      if (args.action !== 'reset_temporary' && !args.elementIds) issue('elementIds', 'Нужен явный список elementIds.');
      if (args.elementIds && new Set(args.elementIds).size !== args.elementIds.length) issue('elementIds', 'Идентификаторы должны быть уникальными.');
      if (args.action === 'set_color' && !args.color) issue('color', 'Укажите RGB-цвет.');
      if (args.action !== 'set_color' && args.color) issue('color', 'color доступен только для set_color.');
      if (args.action === 'set_transparency' && args.transparency === undefined) issue('transparency', 'Укажите прозрачность 0–100.');
      if (!['set_color', 'set_transparency'].includes(args.action) && args.transparency !== undefined) issue('transparency', 'Прозрачность доступна только для set_color/set_transparency.');
    }));

  register('apply_element_graphics',
    'Применить проверенный план графики/видимости после подтверждения в Revit. План связан с документом и его состоянием; изменённый документ требует нового preview. При autoConfirm=true в локальных настройках окно подтверждения пропускается.',
    plan, writeHints);

  register('preview_color_by_parameter',
    'Сгруппировать элементы точной категории на указанном виде по параметру и проверить раскраску с откатом. Preview возвращает значения, количества, IDs и цвета. Режим categorical использует повторяемую палитру, gradient интерполирует числовые значения во внутренних единицах. Отсутствующие/незаполненные значения — серые. Параметры экземпляра и типа выбираются явно. Не более 1000 элементов и 100 групп; превышение отменяет весь запрос.',
    z.object({
      viewId: id,
      category: z.string().min(1).max(200).describe('Точное имя категории в проекте либо имя BuiltInCategory, например OST_Walls.'),
      parameterName: z.string().trim().min(1).max(200),
      parameterScope: z.enum(['instance', 'type']).default('instance'),
      mode: z.enum(['categorical', 'gradient']).default('categorical'),
      palette: z.array(rgb).min(2).max(100).optional().describe('RGB-палитра; в gradient задаёт последовательные точки градиента от минимума к максимуму.'),
      includeMissing: z.boolean().default(true),
      transparency: z.number().int().min(0).max(100).optional(),
      maxElements: z.number().int().min(1).max(1000).default(1000)
    }).strict());

  register('apply_color_by_parameter',
    'Применить ранее проверенную раскраску по параметру после подтверждения в Revit. Несвязанные графические переопределения сохраняются. При autoConfirm=true в локальных настройках окно подтверждения пропускается.',
    plan, writeHints);
}
