import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, readdir, rename, unlink, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { callRevit } from './revit-bridge.js';

const schemaVersion = 1;
const maxRoomSnapshot = 20000;
const pageSize = 500;

function storageRoot() {
  const localAppData = process.env.LOCALAPPDATA;
  return join(localAppData || join(homedir(), 'AppData', 'Local'), 'WEAM.Revit.AI', 'snapshots');
}

function projectKey(projectInfo) {
  const identity = projectInfo.filePath || projectInfo.projectNumber || projectInfo.projectName || 'untitled';
  return createHash('sha256').update(String(identity).toLocaleLowerCase('en-US')).digest('hex').slice(0, 24);
}

async function writeSnapshot(kind, key, data) {
  const directory = join(storageRoot(), kind);
  await mkdir(directory, { recursive: true });
  const destination = join(directory, `${key}.json`);
  const temporary = `${destination}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporary, `${JSON.stringify(data, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' });
    await rename(temporary, destination);
  } catch (error) {
    try { await unlink(temporary); } catch { }
    throw error;
  }
  return destination;
}

async function loadSnapshots(kind) {
  const directory = join(storageRoot(), kind);
  let files;
  try {
    files = await readdir(directory);
  } catch (error) {
    if (error.code === 'ENOENT') return [];
    throw error;
  }

  const rows = [];
  for (const file of files.filter(name => name.endsWith('.json'))) {
    try {
      rows.push(JSON.parse(await readFile(join(directory, file), 'utf8')));
    } catch (error) {
      if (error instanceof SyntaxError) continue;
      throw error;
    }
  }
  return rows;
}

export async function saveProjectSnapshot() {
  const project = await callRevit('get_project_info', {});
  const stats = await callRevit('get_project_stats', {});
  const key = projectKey(project);
  const snapshot = {
    schemaVersion,
    dataset: 'project',
    projectKey: key,
    capturedAt: new Date().toISOString(),
    project,
    stats
  };
  await writeSnapshot('projects', key, snapshot);
  return {
    saved: true,
    dataset: 'project',
    projectName: project.projectName,
    projectKey: key,
    capturedAt: snapshot.capturedAt,
    stats: {
      totalInstances: stats.totalInstances,
      roomCount: stats.roomCount,
      familyTypes: stats.familyTypes,
      views: stats.views,
      sheets: stats.sheets
    }
  };
}

export async function saveRoomSnapshot() {
  const project = await callRevit('get_project_info', {});
  const rooms = [];
  let matchingCount = 0;

  for (let offset = 0; ; offset += pageSize) {
    if (offset >= maxRoomSnapshot) {
      throw new Error(`Room snapshot limit reached (${maxRoomSnapshot}); no snapshot was written.`);
    }
    const page = await callRevit('list_rooms', { offset, limit: pageSize });
    matchingCount = page.matchingCount;
    if (!Array.isArray(page.rooms)) throw new Error('Revit returned an invalid room page.');
    rooms.push(...page.rooms);
    if (!page.hasMore) break;
    if (page.rooms.length === 0) throw new Error('Room paging stopped before all rooms were returned.');
  }

  if (rooms.length !== matchingCount) {
    throw new Error(`Room snapshot incomplete: expected ${matchingCount} rooms, received ${rooms.length}.`);
  }

  const key = projectKey(project);
  const snapshot = {
    schemaVersion,
    dataset: 'rooms',
    projectKey: key,
    capturedAt: new Date().toISOString(),
    project: {
      projectName: project.projectName,
      projectNumber: project.projectNumber,
      filePath: project.filePath
    },
    roomCount: rooms.length,
    rooms
  };
  await writeSnapshot('rooms', key, snapshot);
  return {
    saved: true,
    dataset: 'rooms',
    projectName: project.projectName,
    projectKey: key,
    capturedAt: snapshot.capturedAt,
    roomCount: rooms.length
  };
}

export async function queryStoredData({ query = '', dataset = 'all', limit = 50 }) {
  const needle = String(query).trim().toLocaleLowerCase('ru-RU');
  const boundedLimit = Math.max(1, Math.min(200, Number(limit) || 50));
  const includeProjects = dataset === 'all' || dataset === 'projects';
  const includeRooms = dataset === 'all' || dataset === 'rooms';
  const matches = [];

  if (includeProjects) {
    const snapshots = await loadSnapshots('projects');
    for (const snapshot of snapshots) {
      const searchable = JSON.stringify(snapshot).toLocaleLowerCase('ru-RU');
      if (needle && !searchable.includes(needle)) continue;
      matches.push({
        dataset: 'project',
        projectName: snapshot.project?.projectName,
        projectNumber: snapshot.project?.projectNumber,
        filePath: snapshot.project?.filePath,
        capturedAt: snapshot.capturedAt,
        stats: snapshot.stats
      });
    }
  }

  if (includeRooms) {
    const snapshots = await loadSnapshots('rooms');
    for (const snapshot of snapshots) {
      for (const room of snapshot.rooms || []) {
        const searchable = `${snapshot.project?.projectName || ''} ${snapshot.project?.projectNumber || ''} ${JSON.stringify(room)}`.toLocaleLowerCase('ru-RU');
        if (needle && !searchable.includes(needle)) continue;
        matches.push({
          dataset: 'room',
          projectName: snapshot.project?.projectName,
          projectNumber: snapshot.project?.projectNumber,
          capturedAt: snapshot.capturedAt,
          ...room
        });
      }
    }
  }

  matches.sort((a, b) => String(b.capturedAt).localeCompare(String(a.capturedAt)));
  return {
    query: String(query),
    dataset,
    matchCount: matches.length,
    returnedCount: Math.min(matches.length, boundedLimit),
    limit: boundedLimit,
    results: matches.slice(0, boundedLimit)
  };
}
