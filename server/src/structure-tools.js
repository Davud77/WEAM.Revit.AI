import * as z from 'zod/v4';

const id = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const coordinate = z.number().finite().min(-100000).max(100000);
const plan = z.object({ planId: z.string().min(1).max(80) }).strict();
const write = { readOnlyHint: false, destructiveHint: false, openWorldHint: false };

export function registerStructureTools(register) {
  register('list_beam_types', 'Прочитать загруженные типы конструктивных балок и уровни для балочных систем.', z.object({}).strict());
  register('preview_create_beam_systems', 'Проверить прямоугольные балочные системы с фиксированным шагом. Координаты и шаг в метрах; используется загруженный тип балки.',
    z.object({ systems: z.array(z.object({ levelId: id, beamTypeId: id,
      minX: coordinate, maxX: coordinate, minY: coordinate, maxY: coordinate,
      beamDirection: z.enum(['x', 'y']), spacingMeters: z.number().finite().min(0.1).max(20)
    }).strict().refine(s => s.maxX - s.minX >= 0.5 && s.maxX - s.minX <= 100 &&
      s.maxY - s.minY >= 0.5 && s.maxY - s.minY <= 100)).min(1).max(10) }).strict(), write);
  register('apply_create_beam_systems', 'Создать проверенные балочные системы Revit.', plan, write);
  register('list_phases', 'Прочитать стадии текущего проекта Revit.', z.object({}).strict());
  register('preview_set_view_phase', 'Проверить смену стадии для существующих видов, чтобы на них отображались элементы нужной стадии.',
    z.object({ phaseId: id, viewIds: z.array(id).min(1).max(50).refine(ids => new Set(ids).size === ids.length) }).strict(), write);
  register('apply_set_view_phase', 'Применить проверенную стадию к видам.', plan, write);
}
