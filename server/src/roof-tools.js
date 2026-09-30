import * as z from 'zod/v4';
const id=z.number().int().positive().max(Number.MAX_SAFE_INTEGER);
const n=(min,max)=>z.number().finite().min(min).max(max);
const plan=z.object({planId:z.string().min(1).max(80)}).strict();
const write={readOnlyHint:false,destructiveHint:false,openWorldHint:false};
export function registerRoofTools(register){
  register('preview_finish_sheet','Пробная компоновка листа: положение и масштаб существующих видовых экранов, новые текстовые примечания. Координаты в мм от нижнего левого угла листа; у текста задаётся верхний левый угол. Повторный apply нового плана создаёт дополнительные примечания.',z.object({sheetId:id,viewports:z.array(z.object({viewportId:id,scale:z.number().int().min(1).max(1000),xMm:n(20,415),yMm:n(5,292)}).strict()).max(10).optional(),notes:z.array(z.object({xMm:n(20,410),yMm:n(10,290),widthMm:n(10,390),heightMm:n(1.8,7),text:z.string().min(1).max(4000)}).strict().refine(a=>a.xMm+a.widthMm<=415)).max(20).optional()}).strict().refine(a=>(a.viewports?.length??0)+(a.notes?.length??0)>0),write);
  register('apply_finish_sheet','Применить проверенное оформление листа и создать примечания.',plan,write);
  register('list_roof_types','Прочитать системные типы кровли и толщину в метрах.',z.object({}).strict());
  register('activate_view','Открыть существующий вид в Revit; модель не изменяется.',z.object({viewId:id}).strict(),write);
  register('preview_create_roof','Пробное создание прямоугольной двускатной кровли Revit с откатом. Контур со свесами задаётся в метрах; ridgeAxis задаёт направление конька. При передаче attachWallIds проверяется присоединение стен к кровле.',z.object({roofTypeId:id,levelId:id,minX:n(-100000,100000),maxX:n(-100000,100000),minY:n(-100000,100000),maxY:n(-100000,100000),offsetMeters:n(-100,100),slopeDegrees:n(1,70),ridgeAxis:z.enum(['x','y']),attachWallIds:z.array(id).max(50).refine(a=>new Set(a).size===a.length).optional()}).strict().refine(a=>a.maxX-a.minX>=0.5&&a.maxX-a.minX<=100&&a.maxY-a.minY>=0.5&&a.maxY-a.minY<=100),write);
  register('apply_create_roof','Создать проверенную кровлю и присоединить указанные стены. При autoConfirm=true без окна подтверждения.',plan,write);
  register('preview_create_sections','Пробное создание вертикальных разрезов с откатом. Геометрия в метрах; направление взгляда — единичный вектор XY. Внешние разрезы можно использовать как фасадные виды.',z.object({views:z.array(z.object({name:z.string().min(1).max(150),xMeters:n(-100000,100000),yMeters:n(-100000,100000),bottomMeters:n(-1000,10000),topMeters:n(-1000,10000),widthMeters:n(0.5,200),depthMeters:n(0.1,200),directionX:n(-1,1),directionY:n(-1,1)}).strict().refine(a=>a.topMeters-a.bottomMeters>=0.1&&Math.abs(a.directionX*a.directionX+a.directionY*a.directionY-1)<1e-6)).min(1).max(10)}).strict(),write);
  register('apply_create_sections','Создать проверенные разрезы. При autoConfirm=true без окна подтверждения.',plan,write);
}
