<template>
    <!-- ═══ pb-head(原型下钻屏页头):返回 · 标题 + mono 副行 · 动作组 ═══ -->
    <!-- scrollable: the column (episode table + subtitle files + history)
         exceeds one viewport on a 1080p screen, and an overflow-hidden page
         made everything below the fold unreachable — the whole column
         scrolls instead, mirroring the authentication page form. -->
    <DrillDownPage
        scrollable
        :back-to="{ name: 'library' }"
        :back-label="t('mediaDetail.back')"
        :title="item?.title ?? t('common.loading')"
        :sub="headerMeta">
        <template #actions>
            <!-- library B2: IMDb deep link, only when the payload really
                 carries an id (no static decoration) -->
            <a
                v-if="item?.imdbId"
                :href="`https://www.imdb.com/title/${item.imdbId}/`"
                target="_blank"
                rel="noopener noreferrer"
                class="border-line text-secondary-content hover:bg-tertiary hover:text-primary-content mono inline-flex cursor-pointer items-center rounded-md border px-2.5 py-1 text-xs transition-colors"
                :aria-label="t('mediaDetail.openImdb')"
                data-test="imdb-link">
                IMDb ↗
            </a>
            <button
                v-if="isShow"
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'import', query: { tab: 'season', show: props.id } })">
                {{ t('library.action.seasonImport') }}
            </button>
            <button
                v-else
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                @click="router.push({ name: 'import' })">
                {{ t('library.toolbar.import') }}
            </button>
            <!-- subtitle search entry is per-media: movies open it from here,
                 shows open it per episode row (the search API only accepts
                 movie/episode, a show has no searchable id) -->
            <button
                v-if="!isShow"
                type="button"
                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded-md border px-2.5 py-1 text-xs transition-colors"
                data-test="open-subtitle-search"
                @click="openMovieSearch">
                {{ t('mediaDetail.openSubtitleSearch') }}
            </button>
            <!-- shows: the only in-page「翻译缺失」entry — it opens the
                 language menu before queueing. Movies have no head button
                 here (the label would duplicate the topbar CTA one-for-one,
                 same handler): the CTA is their single entry. -->
            <ContextMenu v-if="isShow" @select="translateMissing">
                <button
                    type="button"
                    class="bg-accent text-accent-content border-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-semibold transition-colors hover:brightness-106">
                    {{ t('mediaDetail.translateMissing') }}
                </button>
            </ContextMenu>
        </template>

        <!-- ═══ 媒体文件批次:海报 + 简介(*arr 详情页口径)—— 两者皆无时整块不渲染 ═══ -->
        <div
            v-if="overviewBlockVisible"
            class="border-line bg-secondary flex shrink-0 gap-4 rounded-md border p-4"
            data-test="media-overview-card">
            <img
                v-if="posterSrc"
                :src="posterSrc"
                class="border-line h-[252px] w-[168px] shrink-0 rounded-md border object-cover"
                :alt="item?.title ?? ''"
                loading="lazy"
                decoding="async"
                data-test="media-poster" />
            <div class="shell-scroll max-h-[252px] min-w-0 flex-1 overflow-y-auto">
                <p
                    v-if="item?.overview"
                    class="text-secondary-content text-[13px] leading-relaxed whitespace-pre-line"
                    data-test="media-overview-text">
                    {{ item.overview }}
                </p>
            </div>
        </div>

        <!-- ═══ 工具栏:季段选(剧集)· 语言 chips · 最近活动 ═══ -->
        <div class="flex shrink-0 flex-wrap items-center gap-2">
            <div
                v-if="isShow"
                class="border-line inline-flex overflow-hidden rounded-md border"
                role="group"
                :aria-label="t('mediaDetail.seasons')">
                <button
                    v-for="season in seasons"
                    :key="season.seasonNumber"
                    type="button"
                    class="border-line text-secondary-content hover:text-primary-content cursor-pointer border-r px-3 py-1.5 text-[12.5px] transition-colors last:border-r-0"
                    :class="
                        activeSeason === season.seasonNumber
                            ? 'bg-tertiary text-primary-content'
                            : ''
                    "
                    :aria-pressed="activeSeason === season.seasonNumber"
                    @click="activeSeason = season.seasonNumber">
                    S{{ String(season.seasonNumber).padStart(2, '0') }}
                </button>
            </div>
            <div class="flex flex-wrap gap-1">
                <span
                    v-for="track in item?.tracks ?? []"
                    :key="track.language"
                    :class="chipClass(movieBusyState(track))"
                    :title="trackTitle(track)"
                    data-test="track-chip">
                    {{ trackLabel(track) }}
                    <!-- B07:forced 维度并入同一枚语言 chip(9b0180f 同概念徽章全页
                         只显一处——不另造第二套徽章体系);have/state 始终是全量口径,
                         仅 forced 的语言维持 none 态,只是如实地多出一枚 forced 标 -->
                    <span
                        v-if="trackForcedCount(track) > 0"
                        class="text-accent"
                        :title="forcedTitle(track)"
                        data-test="track-forced-mark">
                        {{ t('library.track.forcedTag') }}
                    </span>
                    <!-- C5:none 态用样式化状态点元素,不输出尾随文本「·」——裸点会被
                         误读成列表悬空分隔符 -->
                    <span
                        v-if="trackIsNone(track)"
                        class="bg-secondary-content ml-0.5 inline-block h-1.5 w-1.5 rounded-full"
                        aria-hidden="true"
                        data-test="track-none-dot"></span>
                </span>
            </div>
            <span class="mono text-secondary-content ml-auto text-xs">
                {{ t('mediaDetail.lastActivity', { time: lastActivityLabel }) }}
            </span>
        </div>

        <!-- ═══ 剧集:逐集轨道表(虚拟滚动,P2-8:千集季只渲染视口行) ═══ -->
        <template v-if="isShow">
            <!-- C4:卡高度内容自适应(短季不留大片空白),max-h 上限内滚动(长季
                 仍可滚);剩余空间交给下方 Translation history 卡 -->
            <div
                class="border-line bg-secondary flex max-h-[min(70vh,640px)] min-h-0 flex-col rounded-md border"
                data-test="episode-list-card">
                <VirtualEpisodeList
                    :items="seasonEpisodes"
                    :item-height="40"
                    :overscan="5"
                    :colspan="8"
                    :label="t('mediaDetail.episodeTitle')"
                    :key-of="(ep: EpisodeRow) => ep.id">
                    <template #header>
                        <thead>
                            <tr>
                                <th :class="thClass" class="w-11">#</th>
                                <th :class="thClass">{{ t('mediaDetail.episodeTitle') }}</th>
                                <th :class="thClass" class="w-24">
                                    {{ t('mediaDetail.mediaFile.quality') }}
                                </th>
                                <th :class="thClass" class="w-16">
                                    {{ t('mediaDetail.mediaFile.size') }}
                                </th>
                                <th :class="thClass">{{ t('library.table.tracks') }}</th>
                                <th :class="thClass">{{ t('library.table.status') }}</th>
                                <th :class="thClass">{{ t('library.table.lastActivity') }}</th>
                                <th :class="thClass" class="w-36"></th>
                            </tr>
                        </thead>
                    </template>
                    <template #row="{ item: ep }">
                        <tr class="hover:bg-tertiary/50">
                            <td :class="tdClass">
                                <span class="mono text-secondary-content text-[11px]">
                                    E{{ String(ep.episodeNumber).padStart(2, '0') }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <b class="truncate font-semibold" :title="ep.title">
                                    {{ ep.title }}
                                </b>
                            </td>
                            <td :class="tdClass">
                                <span
                                    class="mono text-secondary-content text-[11px]"
                                    :title="episodeSpecTooltip(ep)"
                                    data-test="episode-quality">
                                    {{ ep.quality ?? '—' }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <span
                                    class="mono text-secondary-content text-[11px]"
                                    data-test="episode-size">
                                    {{ ep.sizeBytes ? formatBytes(ep.sizeBytes) : '—' }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <div class="flex flex-wrap gap-1">
                                    <span
                                        v-for="lang in episodeLanguages(ep)"
                                        :key="lang"
                                        :class="
                                            chipClass(episodeHas(ep, lang) ? 'complete' : 'none')
                                        "
                                        data-test="episode-lang-chip">
                                        {{ lang.toUpperCase() }}
                                        <!-- C5:缺失态用样式化状态点,与头部 track chip 一致,
                                             不输出尾随文本「·」 -->
                                        <span v-if="episodeHas(ep, lang)" aria-hidden="true"
                                            >✓</span
                                        >
                                        <span
                                            v-else
                                            class="bg-secondary-content ml-0.5 inline-block h-1.5 w-1.5 rounded-full"
                                            aria-hidden="true"
                                            data-test="episode-lang-dot"></span>
                                    </span>
                                </div>
                            </td>
                            <td :class="tdClass">
                                <span :class="episodeStatusClass(ep)">
                                    {{ episodeStatusLabel(ep) }}
                                </span>
                            </td>
                            <td :class="tdClass">
                                <time class="mono text-secondary-content text-xs">
                                    {{
                                        episodeRequest(ep)
                                            ? formatTime(episodeRequest(ep)!.updatedAt ?? '')
                                            : '—'
                                    }}
                                </time>
                            </td>
                            <td :class="tdClass">
                                <div
                                    class="flex items-center justify-end gap-1.5 whitespace-nowrap">
                                    <button
                                        type="button"
                                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                        data-test="episode-subtitle-search"
                                        @click="openEpisodeSearch(ep)">
                                        {{ t('mediaDetail.openSubtitleSearch') }}
                                    </button>
                                    <router-link
                                        v-if="
                                            episodeRequest(ep)?.status ===
                                            TRANSLATION_STATUS.COMPLETED
                                        "
                                        :to="{
                                            name: 'proofread',
                                            params: { id: episodeRequest(ep)!.id }
                                        }">
                                        <button
                                            type="button"
                                            class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                            :title="t('common.translationAction.proofread')"
                                            :aria-label="
                                                t('common.translationAction.proofread')
                                            "
                                            data-test="episode-proofread">
                                            {{ t('common.translationAction.proofreadShort') }}
                                        </button>
                                    </router-link>
                                    <router-link
                                        v-else-if="episodeDetailRequest(ep)"
                                        :to="{
                                            name: 'queue-detail',
                                            params: { id: episodeDetailRequest(ep)!.id }
                                        }">
                                        <button
                                            type="button"
                                            class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                            {{ t('library.action.detail') }}
                                        </button>
                                    </router-link>
                                    <ContextMenu
                                        v-if="episodeSource(ep)"
                                        :media="ep"
                                        :subtitle="episodeSource(ep)"
                                        media-type="Episode"
                                        @select="translateEpisode(ep, $event)">
                                        <button
                                            type="button"
                                            class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors">
                                            {{ t('mediaDetail.translate') }}
                                        </button>
                                    </ContextMenu>
                                </div>
                            </td>
                        </tr>
                    </template>
                    <template #empty>
                        <span class="text-secondary-content/60 text-xs">
                            {{ t('mediaDetail.noEpisodes') }}
                        </span>
                    </template>
                </VirtualEpisodeList>
            </div>
        </template>

        <!-- ═══ 电影:进行中任务卡(无任务时空态主行动) ═══ -->
        <div
            v-else
            class="border-line bg-secondary flex min-h-[160px] flex-1 flex-col rounded-md border">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('mediaDetail.tasks.heading') }}</h2>
                <span v-if="tasksMeta" class="mono text-secondary-content ml-auto text-xs">
                    {{ tasksMeta }}
                </span>
            </div>
            <div class="shell-scroll flex min-h-0 flex-1 flex-col overflow-y-auto">
                <JobRow v-for="request in activeRequests" :key="request.id" :request="request" />
                <div
                    v-if="item && activeRequests.length === 0"
                    class="flex flex-wrap items-center justify-center gap-3 px-4 py-6 text-center">
                    <!-- C2:全目标语言就绪 → 静默完成态(无 Start translation 按钮) -->
                    <span
                        v-if="movieAllTargetsComplete"
                        class="text-success text-xs"
                        data-test="tasks-all-done">
                        ✓ {{ t('mediaDetail.tasks.allDone') }}
                    </span>
                    <span v-else-if="movieTargetComplete" class="text-success text-xs">
                        ✓ {{ t('mediaDetail.movie.done') }}
                    </span>
                    <template v-else>
                        <span class="text-secondary-content/60 text-xs">
                            {{ t('mediaDetail.movie.untranslated') }}
                        </span>
                        <button
                            type="button"
                            class="bg-accent text-accent-content border-accent cursor-pointer rounded-md border px-2.5 py-1 text-xs font-semibold transition-colors hover:brightness-106"
                            @click="movieTranslateMissing">
                            {{ t('mediaDetail.movie.start') }}
                        </button>
                    </template>
                </div>
            </div>
        </div>

        <!-- ═══ 媒体文件批次:电影媒体文件卡(Sonarr/Radarr 文件块口径)——
             文件名 · 所在目录 · 规格 chips(质量/分辨率/编码/声道) · 大小 · 时长 · 添加日期;
             只有真实存在的字段才渲染行(禁静态装饰) ═══ -->
        <div
            v-if="movieFileCardVisible"
            class="border-line bg-secondary flex shrink-0 flex-col gap-2 rounded-md border p-4"
            data-test="media-file-card">
            <div class="flex flex-wrap items-center gap-2.5">
                <h2 class="text-[13px] font-semibold">
                    {{ t('mediaDetail.mediaFile.title') }}
                </h2>
                <span
                    v-if="movieSpecChips.length"
                    class="flex flex-wrap gap-1"
                    data-test="media-file-specs">
                    <span
                        v-for="chip in movieSpecChips"
                        :key="chip"
                        class="mono border-line bg-tertiary text-primary-content rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold">
                        {{ chip }}
                    </span>
                </span>
                <span
                    v-if="item?.sizeBytes"
                    class="mono text-secondary-content ml-auto text-xs"
                    data-test="media-file-size">
                    {{ formatBytes(item.sizeBytes) }}
                </span>
            </div>
            <div
                class="mono text-primary-content text-[12.5px] break-all"
                data-test="media-file-name">
                {{ item?.fileName }}
            </div>
            <div
                v-if="item?.path"
                class="mono text-secondary-content text-[11.5px] break-all"
                :title="item.path"
                data-test="media-file-directory">
                {{ item.path }}
            </div>
            <div
                v-if="item?.runtimeMinutes || item?.dateAdded"
                class="text-secondary-content flex flex-wrap gap-x-4 text-xs">
                <span v-if="runtimeLabel(item?.runtimeMinutes)" data-test="media-file-runtime">
                    {{ t('mediaDetail.mediaFile.runtime') }}:
                    {{ runtimeLabel(item?.runtimeMinutes) }}
                </span>
                <span v-if="dateLabel(item?.dateAdded)" data-test="media-file-added">
                    {{ t('mediaDetail.mediaFile.added') }}: {{ dateLabel(item?.dateAdded) }}
                </span>
            </div>
        </div>

        <!-- ═══ Subtitle files: external subtitle files actually existing under the directory (ground truth of the covering chips);
             dual-language files (scjp/简日…) one row with two language tags; the embedded track section below shows ffprobe detection
             results (F2b: text tracks with recognized language and uncovered target language have a translate entry; graphic tracks
             such as PGS are marked non-translatable and have no entry) ═══ -->
        <div
            class="border-line bg-secondary flex max-h-[520px] min-h-0 flex-col rounded-md border"
            data-test="subtitle-files-card">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">
                    {{ t('mediaDetail.subtitleFiles.title') }}
                </h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.subtitleFiles.count', { count: subtitleFiles.length }) }}
                </span>
            </div>

            <!-- ═══ 需求卡 B / C1:音轨覆盖徽章行 —— 卡内独立分组(内缩圆角框 + 底色,
                 非普通文件行样式):Audio 徽章 + mono 语言码 + 覆盖说明 + 短标签操作。
                 该行不计入 "N files" 计数;已知音轨且不含目标语言时不渲染任何入口 ═══ -->
            <div v-if="item" class="shrink-0 px-4 pb-1 pt-3" data-test="audio-languages-row">
                <div
                    class="flex flex-wrap items-center gap-x-2.5 gap-y-1 rounded-md border px-3 py-2 text-[12.5px]"
                    :class="
                        audioContainsTarget
                            ? 'border-success/35 bg-success/10'
                            : 'border-line bg-tertiary/50'
                    "
                    data-test="audio-coverage-box">
                    <span
                        class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                        :class="
                            audioContainsTarget
                                ? 'border-success/40 bg-success/15 text-success'
                                : 'border-line bg-tertiary text-secondary-content'
                        "
                        data-test="audio-badge">
                        {{ t('mediaDetail.audio.audioLabel') }}
                    </span>
                    <span
                        v-if="audioLanguages"
                        class="mono text-primary-content text-[11.5px]"
                        data-test="audio-languages-value">
                        {{ audioLanguages.join(' · ') }}
                    </span>
                    <span
                        v-else
                        class="text-secondary-content/70 text-xs"
                        data-test="audio-languages-unknown">
                        {{ t('mediaDetail.audio.unknown') }}
                        <span
                            v-if="item?.suggestedAudioLanguage"
                            class="text-secondary-content/50"
                            :title="t('mediaDetail.audio.suggestedTitle')"
                            data-test="audio-suggested-hint">
                            · {{ t('mediaDetail.audio.suggested', { code: item.suggestedAudioLanguage.toUpperCase() }) }}
                        </span>
                    </span>
                    <!-- 手动标记的持久徽章:标记存在(任一方向)就必须一直可见,
                         「清除标记」是唯一移除途径 -->
                    <span
                        v-if="item?.audioOverride === true"
                        class="mono rounded-[3px] border border-accent/50 bg-accent/15 px-1.5 py-px text-[10.5px] font-bold text-accent"
                        data-test="audio-marked-needed-badge">
                        {{ t('mediaDetail.audio.markedBadge') }}
                    </span>
                    <span
                        v-else-if="item?.audioOverride === false"
                        class="mono border-success/40 bg-success/10 rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold text-success"
                        data-test="audio-marked-not-needed-badge">
                        {{ t('mediaDetail.audio.notNeededBadge') }}
                    </span>
                    <!-- 覆盖说明只在音轨确实覆盖了某目标语言时出现(否则文案失真) -->
                    <span
                        v-if="audioContainsTarget"
                        class="text-secondary-content text-xs"
                        data-test="audio-coverage-note">
                        {{
                            t('mediaDetail.audio.coverageNote', {
                                langs: audioLanguages!.join(', ')
                            })
                        }}
                    </span>
                    <button
                        v-if="subtitleNeedAction"
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors disabled:cursor-default disabled:opacity-50"
                        :disabled="subtitleNeedBusy"
                        :title="subtitleNeedAction.title"
                        :aria-label="subtitleNeedAction.title"
                        data-test="subtitle-need-toggle"
                        @click="toggleSubtitleNeed(subtitleNeedAction.needed)">
                        {{ subtitleNeedAction.label }}
                    </button>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors disabled:cursor-default disabled:opacity-50"
                        :disabled="identifyBusy"
                        :title="t('mediaDetail.audio.identifyTitle')"
                        data-test="identify-audio"
                        @click="identifyAudio">
                        {{ t('mediaDetail.audio.identify') }}
                    </button>
                    <button
                        type="button"
                        class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                        :title="t('mediaDetail.audio.editTitle')"
                        data-test="edit-audio-languages"
                        @click="toggleAudioEditor">
                        {{ t('mediaDetail.audio.edit') }}
                    </button>
                </div>
            </div>

            <!-- E:手动指定音轨语言 —— 常用语言码多选 + 保存/重置 -->
            <div
                v-if="audioEditorOpen"
                class="shrink-0 px-4 pb-3"
                data-test="audio-editor">
                <div
                    class="border-line bg-tertiary/40 flex flex-wrap items-center gap-1.5 rounded-md border px-3 py-2.5">
                    <span class="text-secondary-content mr-1 text-xs">
                        {{ t('mediaDetail.audio.editorLabel') }}
                    </span>
                    <button
                        v-for="code in audioEditorCodes"
                        :key="code"
                        type="button"
                        class="mono rounded-[3px] border px-1.5 py-px text-[11px] font-bold transition-colors"
                        :class="
                            audioEditorSelected.has(code)
                                ? 'border-accent/60 bg-accent/15 text-accent'
                                : 'border-line bg-secondary text-secondary-content hover:text-primary-content'
                        "
                        :aria-pressed="audioEditorSelected.has(code)"
                        :data-test="`audio-editor-code-${code}`"
                        @click="toggleAudioEditorCode(code)">
                        {{ code.toUpperCase() }}
                    </button>
                    <span class="ml-auto flex gap-1.5">
                        <button
                            type="button"
                            class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors disabled:opacity-50"
                            :disabled="audioEditorBusy"
                            data-test="audio-editor-save"
                            @click="saveAudioLanguages">
                            {{ t('common.save') }}
                        </button>
                        <button
                            type="button"
                            class="border-line text-secondary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors disabled:opacity-50"
                            :disabled="audioEditorBusy"
                            :title="t('mediaDetail.audio.resetTitle')"
                            data-test="audio-editor-reset"
                            @click="resetAudioLanguages">
                            {{ t('mediaDetail.audio.reset') }}
                        </button>
                    </span>
                </div>
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <div
                    v-if="subtitleFiles.length === 0"
                    class="text-secondary-content/60 px-4 py-4 text-center text-xs">
                    {{ t('mediaDetail.subtitleFiles.empty') }}
                </div>
                <div
                    v-for="file in subtitleFiles"
                    :key="file.directory + '/' + file.fileName"
                    class="border-line flex flex-wrap items-center gap-x-2.5 gap-y-1 border-b px-4 py-2 text-[12.5px]"
                    :data-test="'subtitle-file-row'">
                    <span
                        class="mono text-primary-content min-w-0 flex-1 truncate text-[11.5px]"
                        :title="file.directory + '/' + file.fileName">
                        {{ file.fileName }}
                    </span>
                    <span class="flex flex-none gap-1">
                        <span
                            v-for="lang in file.languages"
                            :key="lang"
                            class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                            :class="
                                lang === 'unknown'
                                    ? 'border-line bg-tertiary text-secondary-content'
                                    : 'border-success/40 bg-success/10 text-success'
                            ">
                            {{ languageLabel(lang) }}
                        </span>
                    </span>
                    <span class="mono text-secondary-content flex-none text-[10.5px] uppercase">
                        {{ file.format }}
                    </span>
                    <span class="mono text-secondary-content flex-none text-[10.5px]">
                        {{ formatBytes(file.bytes) }}
                    </span>
                </div>

                <!-- ═══ 内嵌轨道分区:每轨一行(语言 chip · 标题 · codec);
                     图形轨(PGS/DVB…)标「图形 · 不可翻译」且无动作入口 ═══ -->
                <div
                    class="border-line/60 bg-tertiary flex items-center gap-2.5 border-y px-4 py-1.5"
                    data-test="embedded-tracks-section">
                    <h3 class="text-secondary-content text-[11px] font-semibold tracking-wide">
                        {{ t('mediaDetail.subtitleFiles.embedded.title') }}
                    </h3>
                    <span class="mono text-secondary-content/70 ml-auto text-[10.5px]">
                        {{
                            t('mediaDetail.subtitleFiles.embedded.count', {
                                count: embeddedTracks.length
                            })
                        }}
                    </span>
                </div>
                <div
                    v-if="!embeddedScanned"
                    class="text-secondary-content/70 px-4 py-3 text-xs"
                    data-test="embedded-not-scanned">
                    {{ t('mediaDetail.subtitleFiles.embedded.notScanned') }}
                </div>
                <div
                    v-else-if="embeddedTracks.length === 0"
                    class="text-secondary-content/60 px-4 py-3 text-xs"
                    data-test="embedded-empty">
                    {{ t('mediaDetail.subtitleFiles.embedded.empty') }}
                </div>
                <template v-else>
                    <div
                        v-for="track in embeddedTracks"
                        :key="`${track.mediaId}-${track.streamIndex}`"
                        class="border-line flex flex-wrap items-center gap-x-2.5 gap-y-1 border-b px-4 py-2 text-[12.5px] last:border-b-0"
                        data-test="embedded-track-row">
                        <span class="mono text-secondary-content/70 w-8 flex-none text-[10.5px]">
                            #{{ track.streamIndex }}
                        </span>
                        <span
                            class="text-primary-content min-w-0 flex-1 truncate text-[12px]"
                            :title="track.title ?? ''">
                            {{ track.title ?? '—' }}
                        </span>
                        <span class="flex flex-none gap-1">
                            <span
                                class="mono rounded-[3px] border px-1.5 py-px text-[10.5px] font-bold"
                                :class="
                                    track.language
                                        ? 'border-success/40 bg-success/10 text-success'
                                        : 'border-line bg-tertiary text-secondary-content'
                                ">
                                {{
                                    track.language
                                        ? languageLabel(track.language)
                                        : (track.rawLanguage ?? t('mediaDetail.lang.unknown'))
                                }}
                            </span>
                            <span
                                v-if="!track.isTextBased"
                                class="mono border-line/60 bg-tertiary text-secondary-content/70 rounded-[3px] border px-1.5 py-px text-[10.5px]">
                                {{ t('mediaDetail.subtitleFiles.embedded.graphic') }}
                            </span>
                        </span>
                        <span class="mono text-secondary-content flex-none text-[10.5px] uppercase">
                            {{ track.codec }}
                        </span>
                        <!-- F2b: 文本轨 + 已识别语言 + 仍有未覆盖的目标语言 → 翻译入口;
                             图形轨/语言未知/目标全覆盖时不渲染 -->
                        <ContextMenu
                            v-if="embeddedTrackTranslatable(track)"
                            @select="(language) => translateEmbeddedTrack(track, language)">
                            <button
                                type="button"
                                class="border-line text-primary-content hover:bg-tertiary cursor-pointer rounded border px-2 py-0.5 text-xs transition-colors"
                                data-test="embedded-translate">
                                {{ t('mediaDetail.subtitleFiles.embedded.translate') }}
                            </button>
                        </ContextMenu>
                    </div>
                </template>
            </div>
        </div>

        <!-- ═══ 翻译历史(请求列表形态;行内详情 → queue-detail) ═══ -->
        <div
            class="border-line bg-secondary flex max-h-[min(50vh,400px)] min-h-[160px] flex-col rounded-md border"
            data-test="history-card">
            <div class="border-line flex shrink-0 items-center gap-2.5 border-b px-4 py-3">
                <h2 class="text-[13px] font-semibold">{{ t('mediaDetail.history') }}</h2>
                <span class="mono text-secondary-content ml-auto text-xs">
                    {{ t('mediaDetail.historyCount', history.length) }}
                </span>
            </div>
            <div
                v-if="history.length === 0"
                class="text-secondary-content/60 px-4 py-5 text-center text-xs">
                {{ t('mediaDetail.noHistory') }}
            </div>
            <div class="shell-scroll min-h-0 flex-1 overflow-y-auto">
                <router-link
                    v-for="request in history"
                    :key="request.id"
                    :to="{ name: 'queue-detail', params: { id: request.id } }"
                    class="border-line hover:bg-tertiary/50 flex cursor-pointer items-center gap-3 border-b px-4 py-2.5 text-[12.5px] last:border-b-0">
                    <div class="min-w-0 flex-1">
                        <b class="block truncate text-[12.5px] font-semibold">
                            {{ request.title }}
                        </b>
                        <span class="text-secondary-content text-[11.5px]">
                            {{ request.sourceLanguage.toUpperCase() }} →
                            {{ request.targetLanguage.toUpperCase() }} ·
                            {{ formatTime(request.createdAt ?? request.updatedAt ?? '') }}
                        </span>
                    </div>
                    <span v-if="request.serviceType" :class="chipClass()">
                        {{ request.serviceType }}
                    </span>
                    <span :class="historyStatusClass(request)">
                        {{ historyStatusLabel(request) }}
                    </span>
                </router-link>
            </div>
        </div>
    </DrillDownPage>

    <!-- 需求卡 A:搜索字幕弹层 —— 一次只针对一个可搜索单元(电影自身 / 某一集);
         audioLanguages 驱动「音轨已覆盖目标」提示,existingLanguages 驱动「已存在」预标 -->
    <SubtitleSearchModal
        v-if="searchTarget && item"
        :media-type="searchTarget.mediaType"
        :media-id="searchTarget.mediaId"
        :title="item.title"
        :episode="searchTarget.episode"
        :audio-languages="audioLanguages"
        :existing-languages="searchExistingLanguages"
        @close="searchTarget = null"
        @downloaded="load" />
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import axios from 'axios'
import ContextMenu from '@/cells/app/layout/ContextMenu.vue'
import JobRow from '@/cells/app/components/JobRow.vue'
import DrillDownPage from '@/cells/app/components/DrillDownPage.vue'
import { mediaService } from '@/cells/app/api/mediaService'
import { subtitleService } from '@/cells/library/services/subtitleService'
import { translationRequestService } from '@/cells/app/api/translationRequestService'
import { useSettingStore } from '@/cells/app/stores/setting'
import { useToastStore } from '@/cells/app/stores/toast'
import SubtitleSearchModal from '@/cells/library/components/SubtitleSearchModal.vue'
import VirtualEpisodeList from '@/cells/library/components/VirtualEpisodeList.vue'
import { useTranslateStore } from '@/cells/app/stores/translate'
import { useUiStore } from '@/cells/app/stores/ui'
import {
    ILanguage,
    MEDIA_TYPE,
    IMediaEmbeddedTrack,
    IMediaLibraryItem,
    IMediaSubtitlesResponse,
    ISeason,
    IShow,
    ISubtitle,
    ISubtitleNeedResponse,
    ITranslationRequest,
    IPagedResult,
    SETTINGS,
    TRANSLATION_STATUS,
    TranslationStatus
} from '@/types'

interface EpisodeRow {
    id: number
    episodeNumber: number
    title: string
    fileName: string
    quality?: string | null
    sizeBytes?: number | null
    videoCodec?: string | null
    audioCodec?: string | null
    audioChannels?: number | null
    resolution?: string | null
    runtimeMinutes?: number | null
    season: ISeason
}

const props = defineProps<{ type: 'show' | 'movie'; id: string }>()

const { t, locale } = useI18n()
const router = useRouter()
const settingStore = useSettingStore()
const toastStore = useToastStore()
const uiStore = useUiStore()
const translateStore = useTranslateStore()
const translationRequestApi = translationRequestService(axios)
const mediaApi = mediaService(axios)
const subtitleApi = subtitleService(axios)

const item = ref<IMediaLibraryItem | null>(null)
const history = ref<ITranslationRequest[]>([])
const requestsByEpisode = ref(new Map<number, ITranslationRequest>())
const seasonSubtitles = ref<ISubtitle[]>([])
/// One payload backs both sections of the「字幕文件」card: external files
/// and the ffprobe-embedded tracks of the last scan (null until loaded).
const subtitlePayload = ref<IMediaSubtitlesResponse | null>(null)
const subtitleFiles = computed(() => subtitlePayload.value?.files ?? [])
const embeddedTracks = computed(() => subtitlePayload.value?.embedded ?? [])
const embeddedScanned = computed(() => subtitlePayload.value?.embeddedScanned ?? false)
const activeSeason = ref<number | null>(null)
const isShow = computed(() => props.type === 'show')
const show = computed<IShow | null>(() => item.value?.show ?? null)
const seasons = computed<ISeason[]>(() => show.value?.seasons ?? [])
/// Full episode list of the active season — the per-episode table windows
/// over it at render time (thousand-episode seasons only render the viewport
/// rows, P2-8), while season-wide actions (translate missing) still walk
/// every episode.
const seasonEpisodes = computed<EpisodeRow[]>(() => {
    const season = seasons.value.find((s) => s.seasonNumber === activeSeason.value)
    return (season?.episodes ?? []).map((ep) => ({
        id: ep.id,
        episodeNumber: ep.episodeNumber,
        title: ep.title,
        fileName: ep.fileName ?? '',
        quality: ep.quality,
        sizeBytes: ep.sizeBytes,
        videoCodec: ep.videoCodec,
        audioCodec: ep.audioCodec,
        audioChannels: ep.audioChannels,
        resolution: ep.resolution,
        runtimeMinutes: ep.runtimeMinutes,
        season: season as ISeason
    }))
})

const trackedLanguages = computed<string[]>(() => {
    const langs = new Set<string>()
    for (const track of item.value?.tracks ?? []) {
        langs.add(track.language)
    }
    return [...langs]
})

const firstTargetLanguage = computed<ILanguage | null>(() => {
    const languages = (settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) ?? []) as ILanguage[]
    return languages[0] ?? null
})

// ── 音轨行 · 字幕需求标记(需求卡 A/B)══════════════════════════

/// Audio languages of the audio row: the subtitle-card payload (per-media
/// scan, refreshed after every mark) wins; the library row backs the card
/// up when that payload failed to load. An empty list carries no language
/// information either — it normalizes to null (unknown) so the row states
/// so honestly instead of rendering an empty value span.
const audioLanguages = computed<string[] | null>(() => {
    const fromPayload = subtitlePayload.value?.audioLanguages
    if (fromPayload && fromPayload.length > 0) return fromPayload
    const fromItem = item.value?.audioLanguages
    if (fromItem && fromItem.length > 0) return fromItem
    return null
})

/// All configured target language codes (lowercased) — the audio row's
/// exemption checks every target, not just the first: with targets
/// [JA, ZH] an audio track in either language exempts the item.
const targetLanguageCodes = computed<string[]>(() => {
    const languages = (settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) ?? []) as ILanguage[]
    return languages.map((language) => language.code.toLowerCase())
})

/// Whether the container audio already covers ANY configured target
/// language — the automatic「无需字幕」exemption basis of requirement card A.
const audioContainsTarget = computed<boolean>(() => {
    const audio = audioLanguages.value
    if (!audio) return false
    return targetLanguageCodes.value.some((target) =>
        audio.some((code) => code.toLowerCase() === target)
    )
})

/// The audio row's single toggle, driven by the persisted audioOverride
/// mark. With a mark in EITHER direction the only action is「清除标记」—
/// it clears the mark so the automatic audio rule applies again (the badge
/// disappears with it). Without a mark:「标记需要字幕」pulls an exempt or
/// audio-unknown item into the needing pool (AC5 reversible); known audio
/// without the target already needs subtitles automatically — no entry.
/// The button shows the SHORT label (C1: row space is tight);
/// `title`/`aria-label` keep the full sentence for hover/SR.
const subtitleNeedAction = computed<
    { label: string; title: string; needed: boolean | null } | null
>(() => {
    const media = item.value
    if (!media) return null
    const markNeeded = {
        label: t('mediaDetail.audio.markNeeded'),
        title: t('mediaDetail.audio.markNeeded'),
        needed: true as boolean | null
    }
    const cancelMark = {
        label: t('mediaDetail.audio.cancelMarkShort'),
        title: t('mediaDetail.audio.cancelMark'),
        needed: null
    }
    if (media.audioOverride !== null && media.audioOverride !== undefined) {
        return cancelMark
    }
    if (media.needsNoSubtitles) return markNeeded
    if (audioLanguages.value === null) return markNeeded
    if (audioContainsTarget.value) return markNeeded
    return null
})

const subtitleNeedBusy = ref(false)

// ── 搜索字幕入口(需求卡 A)══════════════════════════════════════
/// The modal addresses exactly one searchable unit: the movie itself
/// (mediaType "movie") or one episode row ("episode" + that episode's
/// id). Shows as a whole are not a searchable unit on the wire.
interface SearchTarget {
    mediaType: 'movie' | 'episode'
    mediaId: number
    episode?: string
}
const searchTarget = ref<SearchTarget | null>(null)

/// Languages the searched unit already carries subtitles in (external files
/// + embedded tracks, scoped by coveredLanguagesFor to the movie or the one
/// episode) — feeds the modal's「已存在」pre-marks.
const searchExistingLanguages = computed<string[]>(() => {
    if (!item.value || !searchTarget.value) return []
    return [...coveredLanguagesFor(searchTarget.value.mediaId)]
})

function openMovieSearch(): void {
    if (!item.value) return
    searchTarget.value = { mediaType: 'movie', mediaId: item.value.id }
}

function openEpisodeSearch(ep: EpisodeRow): void {
    if (!item.value) return
    const season = activeSeason.value
    searchTarget.value = {
        mediaType: 'episode',
        mediaId: ep.id,
        episode: season
            ? `S${String(season).padStart(2, '0')}E${String(ep.episodeNumber).padStart(2, '0')}`
            : undefined
    }
}

const configuredSourceCodes = computed<string[]>(() => {
    const languages = (settingStore.getSetting(SETTINGS.SOURCE_LANGUAGES) ?? []) as ILanguage[]
    return languages.map((language) => language.code.toLowerCase())
})

/// Language pair shown in the movie head: first configured source that the
/// scan found, arrowed at the first configured target. Null when either
/// side cannot be derived from real data.
const movieLanguagePair = computed<string | null>(() => {
    const tracks = (item.value?.tracks ?? []).filter((track) => track.have > 0)
    const target = firstTargetLanguage.value?.code?.toLowerCase()
    if (!target) return null
    const present = tracks.map((track) => track.language.toLowerCase())
    const source =
        configuredSourceCodes.value.find((code) => present.includes(code)) ??
        present.find((code) => code !== target)
    return source ? `${source.toUpperCase()} → ${target.toUpperCase()}` : null
})

/// Episodes still missing the primary target's subtitles — client-side from
/// the coverage scan the library item already carries. Episodes carrying the
/// persisted no_subtitle_needed exemption owe nothing and are subtracted
/// (gap counts never bill exempt media, matching the pipeline KPI).
const missingTargetEpisodes = computed<number | null>(() => {
    const target = firstTargetLanguage.value?.code
    if (!target || !item.value || !isShow.value) return null
    const track = item.value.tracks.find(
        (candidate) => candidate.language.toLowerCase() === target.toLowerCase()
    )
    const exemptEpisodes = seasons.value.reduce(
        (count, season) =>
            count + season.episodes.filter((episode) => episode.noSubtitleNeeded).length,
        0
    )
    return Math.max(0, item.value.episodeCount - exemptEpisodes - (track?.have ?? 0))
})

const headerMeta = computed(() => {
    if (!item.value) return ''
    if (isShow.value) {
        const missing = missingTargetEpisodes.value
        return (
            t('mediaDetail.seasonCount', item.value.seasonCount) +
            ' · ' +
            t('mediaDetail.episodeCount', item.value.episodeCount) +
            (missing
                ? ' · ' +
                  t('mediaDetail.missingEpisodes', {
                      count: missing,
                      lang: firstTargetLanguage.value!.code.toUpperCase()
                  })
                : '')
        )
    }
    const pair = movieLanguagePair.value
    return pair ? `${t('mediaDetail.movieMeta')} · ${pair}` : t('mediaDetail.movieMeta')
})

const lastActivityLabel = computed(() =>
    item.value?.lastActivityAt ? formatTime(item.value.lastActivityAt) : '—'
)

/// In-flight requests of this media (movie: the request itself; queued ones
/// included — they are the "current tasks" the prototype card summarizes).
const activeRequests = computed<ITranslationRequest[]>(() =>
    history.value
        .filter(
            (request) =>
                request.status === TRANSLATION_STATUS.INPROGRESS ||
                request.status === TRANSLATION_STATUS.PENDING
        )
        .sort(
            (a, b) =>
                Number(b.status === TRANSLATION_STATUS.INPROGRESS) -
                Number(a.status === TRANSLATION_STATUS.INPROGRESS)
        )
)

const tasksMeta = computed(() => {
    const requests = activeRequests.value
    if (requests.length === 0) return ''
    if (requests.length === 1) {
        const request = requests[0]
        const meta = `#${request.id}` + (request.serviceType ? ` · ${request.serviceType}` : '')
        return t('mediaDetail.tasksMeta', { meta })
    }
    return t('mediaDetail.tasksCount', requests.length)
})

/// Active request per target language — backs the busy chip variant
/// ("ZH ⏳ 38%") on the movie toolbar.
const activeByLanguage = computed(() => {
    const map = new Map<string, ITranslationRequest>()
    for (const request of activeRequests.value) {
        map.set(request.targetLanguage.toLowerCase(), request)
    }
    return map
})

const movieTargetComplete = computed(() => {
    const target = firstTargetLanguage.value?.code
    if (!target) return false
    return (item.value?.tracks ?? []).some(
        (track) =>
            track.language.toLowerCase() === target.toLowerCase() && track.have >= track.total
    )
})

/// C2:every configured target language already has its subtitle track in
/// place (tracks state, not history) — the tasks card then shows the silent
/// all-done state instead of "Not translated yet + Start translation".
const movieAllTargetsComplete = computed(() => {
    const targets = targetLanguageCodes.value
    if (targets.length === 0) return false
    const tracks = item.value?.tracks ?? []
    return targets.every((target) =>
        tracks.some(
            (track) => track.language.toLowerCase() === target && track.have >= track.total
        )
    )
})

onMounted(load)
onUnmounted(() => {
    uiStore.clearCta()
    uiStore.setSubOverride('')
})

// live topbar sub mirrors the page header line; watching headerMeta itself
// (a computed over item + t()) re-registers the string on locale switches
watch(
    headerMeta,
    (value) => {
        if (!value) return
        uiStore.setSubOverride(value)
    },
    { immediate: true }
)

async function load(): Promise<void> {
    const mediaType = isShow.value ? 'Show' : 'Movie'
    const page = await mediaApi.library<IPagedResult<IMediaLibraryItem>>({
        pageNumber: 1,
        searchQuery: '',
        sortBy: 'Title',
        ascending: true,
        mediaType,
        id: Number(props.id)
    })
    item.value = page.items[0] ?? null
    if (!item.value) return

    // subtitle files living next to the media (movie dir / every season dir)
    // + embedded container tracks from the last ffprobe scan
    try {
        subtitlePayload.value = await mediaApi.mediaSubtitles<IMediaSubtitlesResponse>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            Number(props.id)
        )
    } catch {
        subtitlePayload.value = null
    }

    // season scan for per-episode language attribution
    if (isShow.value && seasons.value.length > 0) {
        activeSeason.value = seasons.value[0].seasonNumber
        await loadSeasonSubtitles(seasons.value[0])
    }

    // requests: a show's episode history rolls up server-side by the parent
    // show (P1-6 — no episode-id URL list for thousand-episode seasons);
    // movies keep the single media id filter
    const historyPage = isShow.value
        ? await translationRequestApi.requests<IPagedResult<ITranslationRequest>>(
              1,
              '',
              'CreatedAt',
              false,
              null,
              null,
              item.value.id
          )
        : await translationRequestApi.requests<IPagedResult<ITranslationRequest>>(
              1,
              '',
              'CreatedAt',
              false,
              null,
              String(item.value.id)
          )
    history.value = historyPage.items
    const latest = new Map<number, ITranslationRequest>()
    for (const request of history.value) {
        if (request.mediaId == null) continue
        const current = latest.get(request.mediaId)
        if (!current || (request.updatedAt ?? '') > (current.updatedAt ?? '')) {
            latest.set(request.mediaId, request)
        }
    }
    requestsByEpisode.value = latest
}

/// 字幕文件行的语言标签:常见码用本地化名,其余回退原码。
function languageLabel(code: string): string {
    const key = `mediaDetail.lang.${code}`
    const label = t(key)
    return label === key ? code : label
}

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return '—'
    const mb = 1024 * 1024
    if (bytes < 1024) return `${bytes} B`
    if (bytes < mb) return `${(bytes / 1024).toFixed(1)} KB`
    // 媒体文件批次:视频文件普遍 GB 级,超 MB 加 GB/TB 档(字幕文件不受影响)。
    if (bytes < mb * 1024) return `${(bytes / mb).toFixed(1)} MB`
    if (bytes < mb * 1024 * 1024) return `${(bytes / (mb * 1024)).toFixed(1)} GB`
    return `${(bytes / (mb * 1024 * 1024)).toFixed(1)} TB`
}

async function loadSeasonSubtitles(season: ISeason): Promise<void> {
    if (!season.path) {
        seasonSubtitles.value = []
        return
    }
    try {
        seasonSubtitles.value = await subtitleApi.collect<ISubtitle[]>(season.path)
    } catch {
        seasonSubtitles.value = []
    }
}

function episodeSubtitleFiles(ep: EpisodeRow): ISubtitle[] {
    const base = ep.fileName.replace(/\.[^.]+$/, '')
    return seasonSubtitles.value.filter(
        (file) =>
            file.fileName === base ||
            file.fileName.toLowerCase().startsWith(base.toLowerCase() + '.')
    )
}

function episodeLanguages(_ep: EpisodeRow): string[] {
    return trackedLanguages.value
}

function episodeHas(ep: EpisodeRow, lang: string): boolean {
    return episodeSubtitleFiles(ep).some(
        (file) => file.language.toLowerCase() === lang.toLowerCase()
    )
}

function episodeSource(ep: EpisodeRow): ISubtitle | undefined {
    return episodeSubtitleFiles(ep)[0]
}

function episodeRequest(ep: EpisodeRow): ITranslationRequest | null {
    return requestsByEpisode.value.get(ep.id) ?? null
}

/// Episodes with a live or failed request expose it through a「详情」link —
/// the drill-down only exists when the request id is actually known.
const EPISODE_DETAIL_STATUSES: TranslationStatus[] = [
    TRANSLATION_STATUS.INPROGRESS,
    TRANSLATION_STATUS.PENDING,
    TRANSLATION_STATUS.FAILED
]

function episodeDetailRequest(ep: EpisodeRow): ITranslationRequest | null {
    const request = episodeRequest(ep)
    if (!request) return null
    return EPISODE_DETAIL_STATUSES.includes(request.status) ? request : null
}

function episodeStatusClass(ep: EpisodeRow): string {
    const request = episodeRequest(ep)
    if (!request) return 'text-secondary-content/60 text-xs'
    switch (request.status) {
        case TRANSLATION_STATUS.INPROGRESS:
            return 'text-accent text-xs'
        case TRANSLATION_STATUS.COMPLETED:
            return 'text-success text-xs'
        case TRANSLATION_STATUS.FAILED:
            return 'text-danger text-xs'
        case TRANSLATION_STATUS.PENDING:
            return 'text-info text-xs'
        default:
            return 'text-secondary-content text-xs'
    }
}

function episodeStatusLabel(ep: EpisodeRow): string {
    const request = episodeRequest(ep)
    if (!request) return t('library.state.none')
    switch (request.status) {
        case TRANSLATION_STATUS.INPROGRESS:
            return request.progress > 0
                ? t('library.state.inProgressRatio', { progress: request.progress })
                : t('library.state.inProgress')
        case TRANSLATION_STATUS.COMPLETED:
            return t('library.state.completed')
        case TRANSLATION_STATUS.FAILED:
            return t('library.state.failed')
        case TRANSLATION_STATUS.PENDING:
            return t('library.state.queued')
        default:
            return request.status
    }
}

function historyStatusClass(request: ITranslationRequest): string {
    return request.status === TRANSLATION_STATUS.COMPLETED
        ? 'text-success text-xs whitespace-nowrap'
        : request.status === TRANSLATION_STATUS.FAILED
          ? 'text-danger text-xs whitespace-nowrap'
          : 'text-secondary-content text-xs whitespace-nowrap'
}

function historyStatusLabel(request: ITranslationRequest): string {
    // library.state.* 文案本身已带状态符号(✓/✗/◐/⏳),这里不再拼前缀,
    // 否则中英文都会渲染出双符号。
    switch (request.status) {
        case TRANSLATION_STATUS.COMPLETED:
            return t('library.state.completed')
        case TRANSLATION_STATUS.FAILED:
            return t('library.state.failed')
        case TRANSLATION_STATUS.INPROGRESS:
            return t('library.state.inProgress')
        case TRANSLATION_STATUS.PENDING:
            return t('library.state.queued')
        default:
            return request.status
    }
}

async function translateEpisode(ep: EpisodeRow, language: ILanguage): Promise<void> {
    const source = episodeSource(ep)
    if (!source) return
    await translateStore.translateSubtitle(
        ep.id,
        source,
        source.language,
        language,
        MEDIA_TYPE.EPISODE
    )
}

// ── 内嵌轨道翻译(F2b)══════════════════════════════════════

/// Episodes by id across all seasons — show-level embedded rows carry their
/// owning episode's mediaId, this maps it back to the episode's file name.
const episodeById = computed(() => {
    const map = new Map<number, { id: number; fileName?: string | null }>()
    for (const season of seasons.value) {
        for (const episode of season.episodes) {
            map.set(episode.id, episode)
        }
    }
    return map
})

/// Languages the owning media item already covers: external subtitle files
/// plus embedded track languages (the same union the server gates on).
/// Movies own the whole payload; show rows scope external files to the
/// owning episode through base-name matching.
function coveredLanguagesFor(mediaId: number): Set<string> {
    const covered = new Set<string>()
    if (!isShow.value) {
        for (const file of subtitleFiles.value) {
            for (const language of file.languages) covered.add(language.toLowerCase())
        }
        for (const track of embeddedTracks.value) {
            if (track.language) covered.add(track.language.toLowerCase())
        }
        return covered
    }

    const episode = episodeById.value.get(mediaId)
    const base = episode?.fileName ? episode.fileName.replace(/\.[^.]+$/, '').toLowerCase() : null
    for (const file of subtitleFiles.value) {
        if (base && !file.fileName.toLowerCase().startsWith(base + '.')) continue
        for (const language of file.languages) covered.add(language.toLowerCase())
    }
    for (const track of embeddedTracks.value) {
        if (track.mediaId === mediaId && track.language) {
            covered.add(track.language.toLowerCase())
        }
    }
    return covered
}

/// Entry gating: text track + recognized language + at least one configured
/// target language not covered yet. Graphic tracks and unknown-language
/// tracks never get the entry; the server re-checks on initiation.
function embeddedTrackTranslatable(track: IMediaEmbeddedTrack): boolean {
    if (!track.isTextBased || !track.language) return false
    const covered = coveredLanguagesFor(track.mediaId)
    const targets = (settingStore.getSetting(SETTINGS.TARGET_LANGUAGES) ?? []) as ILanguage[]
    return targets.some((target) => !covered.has(target.code.toLowerCase()))
}

async function translateEmbeddedTrack(
    track: IMediaEmbeddedTrack,
    language: ILanguage
): Promise<void> {
    if (!track.language) return
    try {
        const requestId = await translateStore.translateEmbeddedTrack({
            mediaType: isShow.value ? MEDIA_TYPE.EPISODE : MEDIA_TYPE.MOVIE,
            mediaId: track.mediaId,
            streamIndex: track.streamIndex,
            sourceLanguage: track.language,
            targetLanguage: language.code
        })
        toastStore.push({
            message: t('mediaDetail.subtitleFiles.embedded.queuedToast', { id: requestId }),
            type: 'success'
        })
        await load()
        if (settingStore.getSetting(SETTINGS.NAVIGATE_TO_DETAILS_ON_REQUEST) === 'true') {
            await router.push({ name: 'queue-detail', params: { id: requestId } })
        }
    } catch (error) {
        const detail = axios.isAxiosError(error)
            ? ((error.response?.data as { message?: string } | undefined)?.message ?? error.message)
            : String(error)
        toastStore.push({
            message: t('mediaDetail.subtitleFiles.embedded.queuedFailed', { message: detail }),
            type: 'error'
        })
    }
}

/// 「标记需要字幕/清除标记」(需求卡 A/B): the response carries the resulting
/// effective state and the persisted override — both apply in place (the
/// list-derived row object is the same reference), toast, then refresh just
/// the subtitle card so the audio row re-renders from a fresh payload.
/// Never a full page reload.
async function toggleSubtitleNeed(needed: boolean | null): Promise<void> {
    const media = item.value
    if (!media || subtitleNeedBusy.value) return
    subtitleNeedBusy.value = true
    try {
        const result = await mediaApi.subtitleNeed<ISubtitleNeedResponse>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            media.id,
            needed
        )
        media.needsNoSubtitles = result.needsNoSubtitles
        media.audioOverride = result.audioOverride ?? null
        toastStore.push({
            message:
                needed === null
                    ? t('mediaDetail.audio.unmarkedToast')
                    : needed
                      ? t('mediaDetail.audio.markedToast')
                      : t('mediaDetail.audio.notNeededToast'),
            type: 'success'
        })
        try {
            subtitlePayload.value = await mediaApi.mediaSubtitles<IMediaSubtitlesResponse>(
                isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
                media.id
            )
        } catch {
            subtitlePayload.value = null
        }
    } catch (error) {
        const detail = axios.isAxiosError(error)
            ? ((error.response?.data as { message?: string } | undefined)?.message ?? error.message)
            : String(error)
        toastStore.push({
            message: t('mediaDetail.audio.failedToast', { message: detail }),
            type: 'error'
        })
    } finally {
        subtitleNeedBusy.value = false
    }
}

// ── 音轨语言确认(走查 A-E 批次:C 远程识别 + E 手动指定) ──────────

/// Editor vocabulary: the codes the track-title parser and the ASR name map
/// both know — anything a user picks round-trips through the server's
/// normalizer, unknown picks simply drop out.
const AUDIO_EDITOR_CODES = [
    'zh', 'en', 'ja', 'ko', 'fr', 'de', 'es', 'it', 'ru', 'th', 'vi', 'hi', 'pt'
] as const

const identifyBusy = ref(false)
const audioEditorOpen = ref(false)
const audioEditorBusy = ref(false)
const audioEditorSelected = ref<Set<string>>(new Set())
const audioEditorCodes = computed(() => {
    const current = (audioLanguages.value ?? []).map((code) => code.toLowerCase())
    return [...new Set([...current, ...AUDIO_EDITOR_CODES])]
})

function toggleAudioEditor(): void {
    audioEditorOpen.value = !audioEditorOpen.value
    if (audioEditorOpen.value) {
        audioEditorSelected.value = new Set((audioLanguages.value ?? []).map((c) => c.toLowerCase()))
    }
}

function toggleAudioEditorCode(code: string): void {
    const next = new Set(audioEditorSelected.value)
    if (next.has(code)) {
        next.delete(code)
    } else {
        next.add(code)
    }
    audioEditorSelected.value = next
}

/// After any audio-language write the subtitle payload is refreshed so the
/// audio row re-renders from the same source that loaded it.
async function refreshAudioRow(): Promise<void> {
    try {
        subtitlePayload.value = await mediaApi.mediaSubtitles<IMediaSubtitlesResponse>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            item.value!.id
        )
    } catch {
        subtitlePayload.value = null
    }
}

async function identifyAudio(): Promise<void> {
    const media = item.value
    if (!media || identifyBusy.value) return
    identifyBusy.value = true
    try {
        const result = await mediaApi.identifyAudio<{
            languages: string[]
            target: string
            writeBack?: string | null
        }>(isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE, media.id)
        media.audioLanguages = result.languages
        toastStore.push({
            message: t('mediaDetail.audio.identifyDone', {
                langs: result.languages.map((c) => c.toUpperCase()).join(' · '),
                target: result.target
            }) + (result.writeBack ? ` · ${result.writeBack}` : ''),
            type: 'success'
        })
        await refreshAudioRow()
    } catch (error) {
        toastStore.push({
            message: t('mediaDetail.audio.identifyFailed', { message: errorMessage(error) }),
            type: 'error'
        })
    } finally {
        identifyBusy.value = false
    }
}

async function saveAudioLanguages(): Promise<void> {
    const media = item.value
    if (!media || audioEditorBusy.value) return
    audioEditorBusy.value = true
    try {
        const result = await mediaApi.setAudioLanguages<{ languages: string[] }>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            media.id,
            [...audioEditorSelected.value]
        )
        media.audioLanguages = result.languages.length > 0 ? result.languages : null
        audioEditorOpen.value = false
        toastStore.push({
            message:
                result.languages.length > 0
                    ? t('mediaDetail.audio.editDone', {
                          langs: result.languages.map((c) => c.toUpperCase()).join(' · ')
                      })
                    : t('mediaDetail.audio.unknown'),
            type: 'success'
        })
        await refreshAudioRow()
    } catch (error) {
        toastStore.push({
            message: t('mediaDetail.audio.identifyFailed', { message: errorMessage(error) }),
            type: 'error'
        })
    } finally {
        audioEditorBusy.value = false
    }
}

async function resetAudioLanguages(): Promise<void> {
    const media = item.value
    if (!media || audioEditorBusy.value) return
    audioEditorBusy.value = true
    try {
        await mediaApi.setAudioLanguages<{ languages: string[] }>(
            isShow.value ? MEDIA_TYPE.SHOW : MEDIA_TYPE.MOVIE,
            media.id,
            []
        )
        media.audioLanguages = null
        audioEditorOpen.value = false
        toastStore.push({ message: t('mediaDetail.audio.editDone', { langs: '—' }), type: 'success' })
        await refreshAudioRow()
    } catch (error) {
        toastStore.push({
            message: t('mediaDetail.audio.identifyFailed', { message: errorMessage(error) }),
            type: 'error'
        })
    } finally {
        audioEditorBusy.value = false
    }
}

function errorMessage(error: unknown): string {
    return axios.isAxiosError(error)
        ? ((error.response?.data as { message?: string } | undefined)?.message ?? error.message)
        : String(error)
}

/// Screen button + topbar CTA (shared action): queue every episode of the
/// active season that still lacks the target-language track, then report
/// the real count and follow the jobs into the queue screen.
async function translateMissing(language: ILanguage): Promise<void> {
    // season-wide action: walk the full season list, not just the visible page
    const missing = seasonEpisodes.value.filter(
        (ep) => episodeSource(ep) && !episodeHas(ep, language.code)
    )
    if (missing.length === 0) {
        toastStore.push({ message: t('mediaDetail.show.noneQueued'), type: 'info' })
        return
    }
    const pair = `${episodeSource(missing[0])!.language.toUpperCase()} → ${language.code.toUpperCase()}`
    for (const ep of missing) {
        await translateEpisode(ep, language)
    }
    toastStore.push({
        message: t('mediaDetail.show.queuedToast', { count: missing.length, pair }),
        type: 'success'
    })
    await router.push({ name: 'queue' })
}

/// Movie counterpart of「翻译缺失」(screen-08): with a job in flight the
/// action reports the real request id and progress; otherwise it creates
/// the translation for the first configured target from the movie's own
/// source subtitle.
async function movieTranslateMissing(): Promise<void> {
    if (!item.value) return
    const busy =
        activeRequests.value.find((request) => request.status === TRANSLATION_STATUS.INPROGRESS) ??
        activeRequests.value[0]
    if (busy) {
        toastStore.push({
            message: t('mediaDetail.movie.busyToast', { id: busy.id, progress: busy.progress }),
            type: 'info'
        })
        return
    }

    const target = firstTargetLanguage.value
    if (!target) {
        toastStore.push({ message: t('mediaDetail.noTarget'), type: 'info' })
        return
    }
    const files = item.value.subtitleFiles ?? []
    const source =
        files.find((file) => configuredSourceCodes.value.includes(file.language.toLowerCase())) ??
        files[0]
    if (!source) {
        toastStore.push({ message: t('mediaDetail.movie.noSource'), type: 'info' })
        return
    }

    const requestId = await translateStore.translateSubtitle(
        item.value.id,
        source,
        source.language,
        target,
        MEDIA_TYPE.MOVIE
    )
    toastStore.push({
        message: t('mediaDetail.movie.queuedToast', { id: requestId }),
        type: 'success'
    })
    await load()
}

// topbar CTA shares the screen button's action: shows queue for the primary
// target directly (the language menu stays on the screen button, so the CTA
// label says「整季」 to mark that difference), movies run the busy/create
// path verbatim and keep the plain label — the CTA is their only entry.
onMounted(() => {
    uiStore.registerCta(
        { key: isShow.value ? 'mediaDetail.ctaTranslateSeason' : 'mediaDetail.translateMissing' },
        () => {
            if (isShow.value) {
                const target = firstTargetLanguage.value
                if (!target) {
                    toastStore.push({ message: t('mediaDetail.noTarget'), type: 'info' })
                    return
                }
                void translateMissing(target)
                return
            }
            void movieTranslateMissing()
        }
    )
})

// ── chips / formatting (matrix vocabulary) ─────────────────────
const thClass =
    'text-secondary-content border-line whitespace-nowrap border-b px-3 py-2 text-left text-xs font-medium'
const tdClass = 'border-line/50 align-middle border-b px-3 py-2'

function chipClass(state?: string): string {
    const base =
        'mono bg-tertiary border-line text-secondary-content inline-flex items-center gap-1 whitespace-nowrap rounded border px-1.5 py-px text-[11px]'
    if (state === 'complete') return base + ' text-success border-success/40'
    if (state === 'partial') return base + ' text-accent border-accent/45'
    if (state === 'busy') return base + ' text-info border-info/45'
    if (state === 'none') return base + ' opacity-55'
    return base
}

/// Movie toolbar chip state: a target with an in-flight request renders the
/// busy variant instead of the static coverage state.
function movieBusyState(track: { language: string; state: string }): string {
    if (!isShow.value && activeByLanguage.value.has(track.language.toLowerCase())) {
        return 'busy'
    }
    return track.state
}

function trackLabel(track: { language: string; have: number; total: number }): string {
    const lang = track.language.toUpperCase()
    const active = activeByLanguage.value.get(track.language.toLowerCase())
    if (!isShow.value && active) {
        return `${lang} ⏳ ${active.progress}%`
    }
    if (track.have >= track.total) return `${lang} ✓`
    if (track.have > 0) return `${lang} ◐ ${track.have}/${track.total}`
    // none 态只输出语言码,状态点由模板渲染(见 trackIsNone)
    return lang
}

/// None-state track (no subtitles, no live request) — its chip renders a
/// styled status dot ELEMENT instead of a trailing text '·': a bare dot
/// glyph after the code reads like a dangling list separator (C5).
function trackIsNone(track: { language: string; have: number; total: number }): boolean {
    if (!isShow.value && activeByLanguage.value.has(track.language.toLowerCase())) return false
    return track.have === 0 && track.have < track.total
}

function trackTitle(track: { language: string; have: number; total: number }): string {
    if (track.have >= track.total) return t('library.track.complete', { lang: track.language })
    if (track.have > 0)
        return t('library.track.partial', {
            lang: track.language,
            have: track.have,
            total: track.total
        })
    return t('library.track.none', { lang: track.language })
}

/// B07 forced dimension of one track: 0 when the payload predates the
/// field (optional) or the language has no forced-only file.
function trackForcedCount(track: { forcedHave?: number }): number {
    return track.forcedHave ?? 0
}

/// Tooltip of the forced mark — honest about the two shapes: a forced-only
/// language still misses its full subtitle; a covered one carries the forced
/// track alongside.
function forcedTitle(track: {
    language: string
    have: number
    total: number
    forcedHave?: number
}): string {
    const forced = trackForcedCount(track)
    if (track.have > 0) {
        return t('library.track.forcedAlongsideTitle', { count: forced })
    }
    return t('library.track.forcedOnlyTitle', {
        lang: track.language,
        have: forced,
        total: track.total
    })
}

function formatTime(value: string | Date): string {
    return new Date(value).toLocaleTimeString(locale.value, {
        hour: '2-digit',
        minute: '2-digit'
    })
}

/// 媒体文件批次:*arr mediaInfo.resolution ("1920x1080") → 高度短标
/// ("1080p",Sonarr/Radarr 文件块的显示口径);非 WxH 形态原样返回。
function resolutionLabel(resolution?: string | null): string {
    if (!resolution) return ''
    const match = /^(\d+)\s*[x×]\s*(\d+)$/i.exec(resolution.trim())
    if (!match) return resolution
    return `${match[2]}p`
}

/// 媒体文件批次:声道数显示为 *arr 口径的一位小数 ("5.1"/"2.0")。
function channelsLabel(channels?: number | null): string {
    return Number.isFinite(channels) && channels !== null
        ? (channels as number).toFixed(1)
        : ''
}

/// 媒体文件批次:分钟 → 本地化 "X 小时 Y 分" / "X 分钟"。
function runtimeLabel(minutes?: number | null): string {
    if (!minutes || minutes <= 0) return ''
    const hours = Math.floor(minutes / 60)
    const rest = minutes % 60
    if (hours <= 0) return t('mediaDetail.mediaFile.minutes', { minutes: rest })
    return rest === 0
        ? t('mediaDetail.mediaFile.hours', { hours })
        : t('mediaDetail.mediaFile.hoursAndMinutes', { hours, minutes: rest })
}

/// 媒体文件批次:详情页电影规格 chips(有值才进数组,禁静态装饰)。
const movieSpecChips = computed<string[]>(() => {
    if (!item.value || isShow.value) return []
    const chips: string[] = []
    if (item.value.quality) chips.push(item.value.quality)
    const resolution = resolutionLabel(item.value.resolution)
    if (resolution) chips.push(resolution)
    if (item.value.videoCodec) chips.push(item.value.videoCodec)
    if (item.value.audioCodec || item.value.audioChannels) {
        const channels = channelsLabel(item.value.audioChannels)
        chips.push(
            [item.value.audioCodec, channels].filter(Boolean).join(' ')
        )
    }
    return chips
})

/// 媒体文件批次:海报走既有 /api/image 代理(cookie 认证随 <img> 自动携带)。
const posterSrc = computed<string | null>(() => {
    if (!item.value?.posterPath) return null
    return `/api/image/${isShow.value ? 'show' : 'movie'}${item.value.posterPath}`
})

/// 媒体文件批次:海报与简介都不存在时整块不渲染(禁静态装饰文案)。
const overviewBlockVisible = computed<boolean>(
    () => Boolean(posterSrc.value || item.value?.overview)
)

/// 媒体文件批次:电影媒体文件卡 —— 文件名非空才渲染(空路径电影是 *arr
/// 异常载荷的折叠形态,空壳卡是纯装饰)。
const movieFileCardVisible = computed<boolean>(
    () => Boolean(item.value?.fileName) && !isShow.value
)

/// 媒体文件批次:添加日期(日期口径,不带时刻)。
function dateLabel(value?: string | null): string {
    if (!value) return ''
    return new Date(value).toLocaleDateString(locale.value, {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit'
    })
}

/// 媒体文件批次:剧集行质量列的悬浮详情(编码/分辨率/声道/时长,有值才拼)。
function episodeSpecTooltip(ep: EpisodeRow): string {
    const parts: string[] = []
    if (ep.quality) parts.push(ep.quality)
    const resolution = resolutionLabel(ep.resolution)
    if (resolution) parts.push(resolution)
    if (ep.videoCodec) parts.push(ep.videoCodec)
    const channels = channelsLabel(ep.audioChannels)
    if (ep.audioCodec || channels) parts.push([ep.audioCodec, channels].filter(Boolean).join(' '))
    const runtime = runtimeLabel(ep.runtimeMinutes)
    if (runtime) parts.push(runtime)
    return parts.join(' · ')
}
</script>
