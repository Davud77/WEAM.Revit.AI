import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import * as z from 'zod/v4';
import { callRevit } from './revit-bridge.js';
import { queryStoredData, saveProjectSnapshot, saveRoomSnapshot } from './stored-data.js';
import { registerHostedTools } from './hosted-tools.js';
import { registerAnnotationTools } from './annotation-tools.js';
import { registerGraphicsTools } from './graphics-tools.js';
import { registerAuthoringTools } from './authoring-tools.js';
import { registerSheetTools } from './sheet-tools.js';
import { registerRoofTools } from './roof-tools.js';
import { registerMepTools } from './mep-tools.js';
import { registerStructureTools } from './structure-tools.js';

const server = new McpServer({
  name: 'weam-revit-ai',
  version: '0.9.6'
}, {
  instructions: 'Работай с открытой моделью Revit через отдельные инструменты. Начинай с get_project_info. Каждая запись требует соответствующего preview, затем apply: Если revit_ping сообщает autoConfirm=true, apply выполняется без окна; иначе Revit запрашивает подтверждение с отказом по умолчанию. Старые планы недействительны после изменения документа или через 10 минут. Новые инструменты выполняют пробную транзакцию с откатом; ID созданных в preview объектов временные. При удалении зависимостей нужно явно передать allowDependentDeletion=true после просмотра списка. Для точных размеров и координат читай свойства, геометрию, типы и опоры: название типа не гарантирует его толщину. Не исполняй произвольный C#.'
});

function register(name, description, inputSchema, annotations = { readOnlyHint: true, destructiveHint: false, openWorldHint: false }) {
  if (['preview_create_dimensions', 'preview_tag_walls', 'preview_tag_rooms', 'preview_element_graphics', 'preview_color_by_parameter'].includes(name))
    annotations = { ...annotations, readOnlyHint: false };
  server.registerTool(name, {
    description,
    inputSchema,
    annotations
  }, async (args, ctx) => {
    try {
      const result = await callRevit(name, args, { signal: ctx?.mcpReq?.signal });
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    } catch (error) {
      return { isError: true, content: [{ type: 'text', text: error.message }] };
    }
  });
}

const empty = z.object({});
const elementIds = z.array(z.number().int().positive()).min(1).max(100);

register('revit_ping', 'Проверить доступность локального моста и Revit. Не изменяет модель.', empty);
register('get_project_info', 'Получить сведения об открытом проекте, активном виде и версии Revit.', empty);
register('get_current_view_elements', 'Перечислить элементы активного вида. По умолчанию возвращает до 200 элементов.', z.object({
  category: z.string().optional(),
  limit: z.number().int().min(1).max(500).default(200)
}));
register('get_selected_elements', 'Прочитать текущий выбор пользователя в Revit.', empty);
register('search_elements', 'Искать элементы по тексту, идентификатору, категории, классу Revit, семейству, типу и области модели. Можно ограничить поиск активным видом или 3D-объёмом в метрах; явно скрытые в активном виде элементы включаются только при viewScope=activeView и includeHiddenInView=true. Выдача постраничная.', z.object({
  query: z.string().max(200).default(''),
  category: z.string().max(200).optional(),
  revitClass: z.string().max(200).optional(),
  familyName: z.string().max(200).optional(),
  typeName: z.string().max(200).optional(),
  elementId: z.number().int().positive().max(Number.MAX_SAFE_INTEGER).optional(),
  bounds: z.object({
    minX: z.number().finite().min(-100000).max(100000),
    minY: z.number().finite().min(-100000).max(100000),
    minZ: z.number().finite().min(-100000).max(100000),
    maxX: z.number().finite().min(-100000).max(100000),
    maxY: z.number().finite().min(-100000).max(100000),
    maxZ: z.number().finite().min(-100000).max(100000)
  }).strict().optional(),
  viewScope: z.enum(['project', 'activeView']).default('project'),
  includeHiddenInView: z.boolean().default(false),
  offset: z.number().int().min(0).max(100000).default(0),
  limit: z.number().int().min(1).max(500).default(100)
}).refine(args => !args.includeHiddenInView || args.viewScope === 'activeView', {
  path: ['includeHiddenInView'],
  message: 'includeHiddenInView доступен только при viewScope=activeView.'
}));
register('get_project_stats', 'Получить общие количества элементов, категорий, видов, листов, уровней и типов.', empty);
register('list_rooms', 'Перечислить помещения и их площадь, периметр, объём и уровень.', z.object({
  levelName: z.string().optional(),
  offset: z.number().int().min(0).max(100000).default(0),
  limit: z.number().int().min(1).max(500).default(200)
}));
register('get_material_quantities', 'Собрать площади и объёмы материалов по проекту или текущему выделению. Окрашенные поверхности не включаются.', z.object({
  scope: z.enum(['project', 'selection']).default('project')
}));
register('list_family_types', 'Перечислить доступные загруженные типы семейств. Это только чтение.', z.object({
  category: z.string().optional(),
  limit: z.number().int().min(1).max(500).default(200)
}));
register('list_host_types', 'Перечислить системные типы стен, перекрытий или потолков, доступные в проекте. Это только чтение.', z.object({
  hostCategory: z.enum(['walls', 'floors', 'ceilings']),
  limit: z.number().int().min(1).max(500).default(200)
}));
register('list_plan_view_types', 'Перечислить доступные типы планов этажей, потолков и конструктивных планов.', empty);
register('get_element_properties', 'Получить основные свойства и до 40 параметров каждого элемента.', z.object({ elementIds }));
register('select_elements', 'Выбрать указанные элементы в интерфейсе Revit. Модель не меняется.', z.object({ elementIds }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_set_parameter', 'Подготовить предварительный просмотр пакетного изменения одного параметра. Ничего не меняет.', z.object({
  elementIds,
  parameterName: z.string().min(1).max(200),
  value: z.string().max(1000)
}));
register('apply_parameter_plan', 'Показать подтверждение в Revit и применить ранее подготовленный план параметра. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_levels', 'Проверить имена и отметки новых уровней. Ничего не создаёт.', z.object({
  levels: z.array(z.object({ name: z.string().min(1).max(100), elevationMeters: z.number().finite().min(-1000).max(10000) })).min(1).max(30)
}));
register('apply_create_levels', 'Показать подтверждение в Revit и создать уровни из подготовленного плана. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_walls', 'Проверить координаты, типы и уровни до создания прямых стен. Координаты X/Y задаются в метрах от внутреннего начала проекта.', z.object({
  walls: z.array(z.object({
    start: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    end: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    baseLevelName: z.string().min(1).max(100),
    wallTypeName: z.string().min(1).max(200),
    heightMeters: z.number().finite().min(0.1).max(100),
    baseOffsetMeters: z.number().finite().min(-100).max(100).optional(),
    flip: z.boolean().optional(),
    structural: z.boolean().optional()
  })).min(1).max(50)
}));
register('apply_create_walls', 'Показать в Revit предварительный список, запросить подтверждение и создать прямые стены. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_place_family_instances', 'Проверить типы семейств, уровни и координаты перед размещением экземпляров семейств.', z.object({
  instances: z.array(z.object({
    familyName: z.string().min(1).max(200),
    typeName: z.string().min(1).max(200),
    levelName: z.string().min(1).max(100),
    position: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    offsetMeters: z.number().finite().min(-100).max(100).optional(),
    rotationDegrees: z.number().finite().min(-36000).max(36000).optional(),
    structuralType: z.enum(['nonStructural', 'beam', 'brace', 'column', 'footing']).optional()
  })).min(1).max(100)
}));
register('apply_place_family_instances', 'Показать список экземпляров, запросить подтверждение в Revit и разместить их. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_floors', 'Проверить горизонтальные замкнутые контуры, типы и уровни новых перекрытий.', z.object({
  floors: z.array(z.object({
    floorTypeName: z.string().min(1).max(200),
    levelName: z.string().min(1).max(100),
    vertices: z.array(z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) })).min(3).max(100),
    heightOffsetMeters: z.number().finite().min(-100).max(100).optional(),
    structural: z.boolean().optional()
  })).min(1).max(10)
}));
register('apply_create_floors', 'Показать подтверждение в Revit и создать перекрытия из проверенного плана. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_grids', 'Проверить имена и прямые линии новых координационных осей.', z.object({
  grids: z.array(z.object({
    name: z.string().min(1).max(100),
    start: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    end: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    elevationMeters: z.number().finite().min(-1000).max(10000).optional()
  })).min(1).max(50)
}));
register('apply_create_grids', 'Показать список осей, запросить подтверждение в Revit и создать их. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_set_category_visibility', 'Проверить вид и категорию перед изменением видимости категории в текущем виде.', z.object({
  categoryName: z.string().min(1).max(200),
  hidden: z.boolean()
}));
register('apply_set_category_visibility', 'Запросить подтверждение в Revit и изменить видимость категории в проверенном виде. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_beams', 'Проверить семейства балок, уровни и 3D-концы до создания конструктивных балок.', z.object({
  beams: z.array(z.object({
    familyName: z.string().min(1).max(200),
    typeName: z.string().min(1).max(200),
    levelName: z.string().min(1).max(100),
    start: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000), zMeters: z.number().finite().min(-100000).max(100000) }),
    end: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000), zMeters: z.number().finite().min(-100000).max(100000) })
  })).min(1).max(50)
}));
register('apply_create_beams', 'Показать тип, уровень и геометрию балок, запросить подтверждение и создать их. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_ceilings', 'Проверить замкнутые горизонтальные контуры, типы и уровни новых потолков.', z.object({
  ceilings: z.array(z.object({
    ceilingTypeName: z.string().min(1).max(200),
    levelName: z.string().min(1).max(100),
    vertices: z.array(z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) })).min(3).max(100),
    heightOffsetMeters: z.number().finite().min(-100).max(100).optional()
  })).min(1).max(10)
}));
register('apply_create_ceilings', 'Показать подтверждение в Revit и создать потолки из проверенного плана. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_rooms', 'Проверить уровни, точки и номера до размещения помещений внутри замкнутых границ.', z.object({
  rooms: z.array(z.object({
    levelName: z.string().min(1).max(100),
    location: z.object({ xMeters: z.number().finite().min(-100000).max(100000), yMeters: z.number().finite().min(-100000).max(100000) }),
    number: z.string().max(50).optional(),
    name: z.string().max(200).optional()
  })).min(1).max(50)
}));
register('apply_create_rooms', 'Показать точки размещения, запросить подтверждение и создать помещения. Операция откатывается, если хотя бы одна точка не попала в замкнутую область. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_create_plan_views', 'Проверить тип, уровни и имена новых планов видов.', z.object({
  viewFamily: z.enum(['FloorPlan', 'CeilingPlan', 'StructuralPlan']),
  viewTypeName: z.string().max(200).optional(),
  views: z.array(z.object({
    levelName: z.string().min(1).max(100),
    viewName: z.string().max(200).optional()
  })).min(1).max(30)
}));
register('apply_create_plan_views', 'Показать имена планов, запросить подтверждение и создать виды на выбранных уровнях. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80) }), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('preview_delete_elements', 'Пробное удаление с полным откатом: возвращает затрагиваемые ID и зависимые элементы. Изменения в модели не сохраняются.', z.object({ elementIds }).strict(), { readOnlyHint: false, destructiveHint: false, openWorldHint: false });
register('apply_delete_plan', 'Удалить проверенный набор после подтверждения в Revit. Если preview показал зависимости, нужно явное allowDependentDeletion=true. Изменение документа требует нового preview. При autoConfirm=true в локальных настройках окно подтверждения пропускается.', z.object({ planId: z.string().min(1).max(80), allowDependentDeletion: z.boolean().default(false) }).strict(), { readOnlyHint: false, destructiveHint: true, openWorldHint: false });

registerHostedTools(register);
registerAnnotationTools(register);
registerGraphicsTools(register);
registerAuthoringTools(register);
registerSheetTools(register);
registerRoofTools(register);
registerMepTools(register);
registerStructureTools(register);

function registerLocal(name, description, inputSchema, handler, annotations = { readOnlyHint: false, destructiveHint: false, openWorldHint: false }) {
  server.registerTool(name, { description, inputSchema, annotations }, async args => {
    try {
      const result = await handler(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    } catch (error) {
      return { isError: true, content: [{ type: 'text', text: error.message }] };
    }
  });
}

registerLocal('store_project_data', 'Сохранить локальный снимок сведений о проекте и статистики текущей модели.', empty, saveProjectSnapshot);
registerLocal('store_room_data', 'Сохранить локальный снимок всех помещений текущей модели для последующего поиска при закрытом Revit.', empty, saveRoomSnapshot);
registerLocal('query_stored_data', 'Искать в локально сохранённых снимках проектов и помещений. Revit может быть закрыт.', z.object({
  query: z.string().max(200).default(''),
  dataset: z.enum(['all', 'projects', 'rooms']).default('all'),
  limit: z.number().int().min(1).max(200).default(50)
}), queryStoredData, { readOnlyHint: true, destructiveHint: false, openWorldHint: false });

console.error('WEAM.Revit.AI MCP server starting on stdio.');
void serveStdio(() => server);
