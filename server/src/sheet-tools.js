import * as z from 'zod/v4';
const id=z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const mm=z.number().finite().min(0).max(420);
const coordinate=z.number().finite().min(-100000).max(100000);
const bounds=z.object({minX:coordinate,minY:coordinate,minZ:coordinate,maxX:coordinate,maxY:coordinate,maxZ:coordinate}).strict()
  .refine(b=>b.minX<b.maxX&&b.minY<b.maxY&&b.minZ<b.maxZ,{message:'Crop bounds must have positive extents.'});
export function registerSheetTools(register){
  register('list_sheet_resources','Прочитать загруженные основные надписи, их параметры, листы и размещённые виды с размерами на бумаге.',z.object({}).strict());
  register('preview_create_sheets','Проверить создание листов А3 (420 × 297 мм), дубликатов видов с оформлением и экспликации помещений. Реальное создание с откатом. Центры видов и позиция спецификации заданы в мм от нижнего левого угла листа; спецификация растёт вниз от указанной точки. Исходные виды не меняются. Основная надпись должна быть загружена; соответствие ГОСТ проверяется по её содержимому, а не названию.',z.object({
    titleBlockTypeId:id,
    sheets:z.array(z.object({number:z.string().trim().min(1).max(50),name:z.string().trim().min(1).max(200),
      titleBlockParameters:z.record(z.string(),z.string()).optional(),
      views:z.array(z.object({sourceViewId:id,viewName:z.string().trim().min(1).max(200),scale:z.number().int().min(1).max(1000),centerXmm:mm,centerYmm:mm,crop:bounds.optional()}).strict()).min(1).max(8),
      roomSchedule:z.object({name:z.string().trim().min(1).max(200),levelId:id,xMm:mm,yMm:mm}).strict().optional()
    }).strict()).min(1).max(10)
  }).strict(),{readOnlyHint:false,destructiveHint:false,openWorldHint:false});
  register('apply_create_sheets','Создать проверенные листы А3 и виды. При autoConfirm=true окно подтверждения пропускается.',z.object({planId:z.string().min(1).max(80)}).strict(),{readOnlyHint:false,destructiveHint:false,openWorldHint:false});
  register('export_sheets_pdf','Выгрузить существующие листы Revit в один PDF с исходным размером бумаги. Нужен новый абсолютный локальный путь .pdf; существующие файлы не перезаписываются.',z.object({sheetIds:z.array(id).min(1).max(20),path:z.string().min(1).max(240)}).strict(),{readOnlyHint:false,destructiveHint:false,openWorldHint:false});
}
