import { describe, expect, it } from 'vitest'
import * as fs from 'node:fs'
import * as path from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * Architecture guard for the cell structure (batch Z of the migration plan).
 *
 * Layer rules enforced here (see docs/cell-migration-plan.zh-CN.md):
 * - kernel   : zero business dependencies. May only reach vue / vue-i18n /
 *              @/types / kernel internals.
 * - app      : application assembly. May use kernel; must not use business
 *              cells — the single exception is `app/router`, the composition
 *              root whose route table lazily imports every cell's pages.
 * - business : each cell is self-contained. Outside its own directory it may
 *              only use kernel, app, @/types and the kept globals (@/i18n,
 *              @/assets). Cells never import each other.
 * - legacy   : the pre-migration top-level directories (components, pages,
 *              store, services, composables, utils, router, directives) were
 *              deleted in batch Z; no import anywhere may point at them.
 */

const CELLS_ROOT = fileURLToPath(new URL('.', import.meta.url))
const SRC_ROOT = path.resolve(CELLS_ROOT, '..')

const BUSINESS_CELLS = new Set([
    'settings',
    'auth',
    'dashboard',
    'proofread',
    'queue',
    'library',
    'import'
])

/** Deleted pre-migration directories — importing one is always a violation. */
const LEGACY_TOP = new Set([
    'components',
    'pages',
    'store',
    'services',
    'composables',
    'utils',
    'router',
    'directives'
])

/** Global pieces that survive the migration (plan decision: i18n stays put). */
const KEPT_GLOBALS = new Set(['types', 'i18n', 'assets'])

interface ImportOccurrence {
    file: string
    line: number
    source: string
}

const listFiles = (dir: string): string[] => {
    const out: string[] = []
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name)
        if (entry.isDirectory()) out.push(...listFiles(full))
        else if (/\.(ts|vue)$/.test(entry.name)) out.push(full)
    }
    return out
}

/** Collects static `from '…'`, side-effect `import '…'` and `import('…')`. */
const collectImports = (file: string): ImportOccurrence[] => {
    const occurrences: ImportOccurrence[] = []
    const lines = fs.readFileSync(file, 'utf8').split('\n')
    lines.forEach((text, index) => {
        const re = /(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s+)['"]([^'"]+)['"]/g
        for (const match of text.matchAll(re)) {
            occurrences.push({
                file,
                line: index + 1,
                source: match[1]
            })
        }
    })
    return occurrences
}

const cellOf = (file: string): string | null => {
    const relative = path.relative(CELLS_ROOT, path.dirname(file))
    if (relative === '') return null // files sitting directly in cells/
    return relative.split(path.sep)[0]
}

const isPackage = (spec: string) => !spec.startsWith('.') && !spec.startsWith('@/')

const aliasTarget = (spec: string) => spec.slice(2) // '@/cells/app/…' -> 'cells/app/…'

const isUnder = (resolved: string, dir: string) =>
    resolved === dir || resolved.startsWith(dir + path.sep)

/** First src/ segment of an absolute path inside src/, e.g. 'components'. */
const srcTopSegment = (resolved: string): string | null => {
    const relative = path.relative(SRC_ROOT, resolved)
    if (relative.startsWith('..')) return null
    return relative.split(path.sep)[0]
}

const formatViolation = (rule: string, imp: ImportOccurrence): string => {
    const line = fs.readFileSync(imp.file, 'utf8').split('\n')[imp.line - 1].trim()
    const where = `${path.relative(SRC_ROOT, imp.file)}:${imp.line}`
    return `${where} [${rule}] ${line}`
}

interface Violation {
    rule: string
    imp: ImportOccurrence
}

const scanCells = (): Violation[] => {
    const violations: Violation[] = []
    const kernelDir = path.join(CELLS_ROOT, 'kernel')
    const appDir = path.join(CELLS_ROOT, 'app')
    const routerDir = path.join(CELLS_ROOT, 'app', 'router')

    for (const file of listFiles(CELLS_ROOT)) {
        const cell = cellOf(file)
        for (const imp of collectImports(file)) {
            const spec = imp.source
            let resolved: string | null = null
            if (spec.startsWith('.')) {
                resolved = path.resolve(path.dirname(file), spec)
            }

            if (isPackage(spec)) continue

            if (spec.startsWith('@/')) {
                const target = aliasTarget(spec)
                const [first, second] = target.split('/')

                // Legacy aliases are banned everywhere under src/cells.
                if (LEGACY_TOP.has(first)) {
                    violations.push({ rule: `legacy @/${first}`, imp })
                    continue
                }

                if (cell === 'kernel') {
                    if (first === 'cells' && second === 'app') {
                        violations.push({ rule: 'kernel→app', imp })
                    } else if (first === 'cells' && second && BUSINESS_CELLS.has(second)) {
                        violations.push({ rule: `kernel→${second}`, imp })
                    }
                } else if (cell === 'app') {
                    const isRouteTable = isUnder(file, routerDir)
                    if (first === 'cells' && second && BUSINESS_CELLS.has(second) && !isRouteTable) {
                        violations.push({ rule: `app→${second}`, imp })
                    }
                } else if (cell && BUSINESS_CELLS.has(cell)) {
                    const allowed =
                        KEPT_GLOBALS.has(first) ||
                        (first === 'cells' && (second === 'kernel' || second === 'app' || second === cell))
                    if (!allowed) {
                        const rule =
                            first === 'cells' && second && BUSINESS_CELLS.has(second)
                                ? `${cell}→${second} (cell-to-cell)`
                                : `${cell}→${first}`
                        violations.push({ rule, imp })
                    }
                }
            } else if (resolved) {
                // Relative import: check where it actually lands.
                const legacyHit = srcTopSegment(resolved)
                const landsInLegacy = legacyHit !== null && LEGACY_TOP.has(legacyHit)
                const landsInCell =
                    isUnder(resolved, CELLS_ROOT) && !isUnder(resolved, path.join(CELLS_ROOT, 'kernel'))

                if (cell === 'kernel') {
                    // May not escape kernel into another cell or a legacy dir.
                    if (!isUnder(resolved, kernelDir) && landsInCell) {
                        violations.push({ rule: 'kernel→escape (relative)', imp })
                    } else if (!isUnder(resolved, kernelDir) && landsInLegacy) {
                        violations.push({ rule: 'kernel→legacy (relative)', imp })
                    }
                } else if (cell === 'app') {
                    const stays = isUnder(resolved, appDir) || isUnder(resolved, kernelDir)
                    const businessCell =
                        BUSINESS_CELLS.has(
                            path.relative(CELLS_ROOT, resolved).split(path.sep)[0]
                        ) && isUnder(resolved, CELLS_ROOT)
                    if (businessCell && !isUnder(file, routerDir)) {
                        violations.push({ rule: 'app→business (relative)', imp })
                    } else if (!stays && landsInLegacy) {
                        violations.push({ rule: 'app→legacy (relative)', imp })
                    }
                } else if (cell && BUSINESS_CELLS.has(cell)) {
                    const cellDir = path.join(CELLS_ROOT, cell)
                    const allowed =
                        isUnder(resolved, cellDir) ||
                        isUnder(resolved, kernelDir) ||
                        isUnder(resolved, appDir) ||
                        (legacyHit !== null && KEPT_GLOBALS.has(legacyHit))
                    if (!allowed) {
                        const rule = landsInLegacy
                            ? `${cell}→legacy (relative)`
                            : `${cell}→escape (relative)`
                        violations.push({ rule, imp })
                    }
                } else if (landsInLegacy) {
                    violations.push({ rule: 'legacy (relative)', imp })
                }
            }
        }
    }
    return violations
}

describe('cells architecture guard', () => {
    const violations = scanCells()

    it('kernel stays pure: no app, business-cell or legacy imports', () => {
        const kernelIssues = violations
            .filter((v) => cellOf(v.imp.file) === 'kernel')
            .map((v) => formatViolation(v.rule, v.imp))
        expect(kernelIssues, 'kernel purity violations').toEqual([])
    })

    it('app never imports business cells (except the router composition root)', () => {
        const appIssues = violations
            .filter((v) => cellOf(v.imp.file) === 'app' && !v.rule.startsWith('legacy'))
            .map((v) => formatViolation(v.rule, v.imp))
        expect(appIssues, 'app isolation violations').toEqual([])
    })

    it('business cells only import kernel / app / @types / kept globals / their own files', () => {
        const cellIssues = violations
            .filter((v) => {
                const cell = cellOf(v.imp.file)
                return cell !== null && BUSINESS_CELLS.has(cell) && !v.rule.startsWith('legacy')
            })
            .map((v) => formatViolation(v.rule, v.imp))
        expect(cellIssues, 'business-cell isolation violations').toEqual([])
    })

    it('no import anywhere under cells references a deleted legacy directory', () => {
        const legacyIssues = violations
            .filter((v) => v.rule.startsWith('legacy'))
            .map((v) => formatViolation(v.rule, v.imp))
        expect(legacyIssues, 'legacy-directory references').toEqual([])
    })
})
