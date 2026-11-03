import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct LyricSourcesPane: View {
    @ObservedObject private var options=LyricSourceOptions.shared
    @ObservedObject private var model=LyricsViewModel.shared
    @ObservedObject private var nowPlaying=NowPlayingMonitor.shared
    @ObservedObject private var settings=Settings.shared
    @ObservedObject private var local=LocalLyricOptions.shared
    @ObservedObject private var songOffsets = SongOffsets.shared
    @State private var title=""
    @State private var artist=""
    @State private var importError:String?
    private let columns=[GridItem(.flexible()),GridItem(.flexible())]

    var body:some View {
        ScrollView {
            VStack(alignment:.leading,spacing:22) {
                PaneHeader(title:"Lyric sources",subtitle:"Find the right recording, compare sources, and keep your choice.")
                localLyricsControls

                VStack(alignment:.leading,spacing:12) {
                    HStack {
                        Text("Matching").font(AppFont.ui(12,.medium))
                        Spacer()
                        ChipRow(items:LyricSourceOptions.Mode.allCases,title:{$0.title},selection:$options.mode)
                    }
                    Text(options.mode == .smart
                         ? "Compare enabled sources, preferring matching recordings and real word timestamps."
                         : "Choose the first available source in your order. Use the arrows to change priority.")
                        .font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.5))
                    LazyVGrid(columns:columns,spacing:9) {
                        ForEach(options.order) { source in sourceCard(source) }
                    }
                    HStack {
                        Text("\(options.enabled.count) of \(LyricSourceID.allCases.count) enabled")
                            .font(AppFont.ui(10.5)).foregroundStyle(.white.opacity(0.45))
                        Spacer()
                        if model.isLoading {ProgressView().controlSize(.small)}
                        Button("Test enabled sources") {model.refresh(force:true)}
                            .disabled(model.currentQuery == nil || model.isLoading)
                    }
                    if model.currentQuery == nil {
                        Text("Play a song in Apple Music to test sources or import lyrics.")
                            .font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.5))
                    } else if options.enabled.isEmpty {
                        Text("Enable a source or import an LRC file.").font(AppFont.ui(11)).foregroundStyle(.orange)
                    }
                }
                .padding(16).background(RoundedRectangle(cornerRadius:12).fill(.white.opacity(0.045)))

                VStack(alignment:.leading,spacing:12) {
                    HStack {
                        Text("Find another match").font(AppFont.ui(14,.semibold))
                        Spacer()
                        Button("Use playing song") {fillTrack()}
                            .font(AppFont.ui(10.5)).disabled(model.currentQuery == nil)
                    }
                    HStack(spacing:10) {
                        TextField("Song title",text:$title)
                        TextField("Artist",text:$artist)
                    }
                    .textFieldStyle(.roundedBorder)
                    HStack {
                        Button("Search enabled sources") {model.refresh(force:true,title:title,artist:artist)}
                            .disabled(model.currentQuery == nil || model.isLoading || title.isEmpty)
                        Button("Import LRC…") {importFile()}.disabled(model.currentQuery == nil)
                    }
                    if let provenance=model.provenance {
                        HStack {
                            Text(provenance.label).font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.65))
                            Spacer()
                            if model.pinnedMatch {
                                Text("Saved choice").font(AppFont.ui(10)).foregroundStyle(.mint)
                                Button("Forget") {model.forgetSelection()}.font(AppFont.ui(10))
                            }
                        }
                    }
                    ForEach(model.candidates) { candidate in
                        Button {model.select(candidate)} label:{
                            HStack(spacing:12) {
                                VStack(alignment:.leading,spacing:4) {
                                    Text(candidate.title+" — "+candidate.artist).font(AppFont.ui(11.5,.medium)).lineLimit(2)
                                    Text(candidate.provenance.label+" · \(candidate.lineCount) lines")
                                        .font(AppFont.ui(10)).foregroundStyle(.white.opacity(0.5))
                                }
                                Spacer()
                                Text("Use match").font(AppFont.ui(10.5)).foregroundStyle(Verso.cream)
                            }
                            .padding(12).background(RoundedRectangle(cornerRadius:9).fill(.white.opacity(0.06)))
                        }.buttonStyle(.plain)
                    }
                    if let message=model.cacheMessage ?? importError {
                        Text(message).font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.6))
                    }
                }
                VStack(alignment:.leading,spacing:8) {
                    HStack {
                        Text("Global timing offset").font(AppFont.ui(12,.medium))
                        Spacer()
                        Text(String(format:"%+.1f s",settings.lyricOffset))
                            .font(AppFont.ui(11)).monospacedDigit().foregroundStyle(.white.opacity(0.6))
                        Button("Reset"){settings.lyricOffset=0}.font(AppFont.ui(10))
                    }
                    Slider(value:$settings.lyricOffset,in:-3...3,step:0.05)
                    Text("Positive values bring lyrics in earlier. Line-only sources use estimated word timing.")
                        .font(AppFont.ui(10.5)).foregroundStyle(.white.opacity(0.45))
                    if let query = model.currentQuery {
                        Text("Offset for \(query.title)").font(AppFont.ui(12, .medium))
                        HStack {
                            Slider(value: Binding(get: { songOffsets.offset(for: query) },
                                set: { songOffsets.set($0, for: query) }), in: -10...10, step: 0.05)
                            Text(String(format: "%+.2f s", songOffsets.offset(for: query))).monospacedDigit()
                            Button("Reset song") { songOffsets.set(0, for: query) }
                        }
                        Text("Saved automatically for this recording. Added to the global offset.").font(AppFont.ui(10.5))
                    }
                    Toggle("Prefer LRCLIB when it has real word timing", isOn: $options.preferWordTimedLRCLIB)
                        .font(AppFont.ui(11))
                    Text("Saved song choices always win. Turn this off to follow source order or match scores exactly.").font(AppFont.ui(10.5))
                    Toggle("Treat long QQ pauses as instrumental", isOn: $options.repairQQGaps)
                        .font(AppFont.ui(11)).toggleStyle(.switch).controlSize(.small)
                    Text("Estimates the end of a line before a long musical gap. Turn this off for a song with unusually long held vocals. Supplied word timestamps stay intact.")
                        .font(AppFont.ui(10.5)).foregroundStyle(.white.opacity(0.45))
                }
                Text("Searches send song details to the enabled services. Source availability varies; no audio or account cookies are uploaded.")
                    .font(AppFont.ui(10)).foregroundStyle(.white.opacity(0.35)).fixedSize(horizontal:false,vertical:true)
            }
            .padding(30)
        }
        .background(Verso.surface)
        .onAppear {fillTrack()}
        .onChange(of:nowPlaying.current) {_,_ in fillTrack()}
    }
    private func fillTrack() {
        title=nowPlaying.current?.title ?? ""
        artist=nowPlaying.current?.artist ?? ""
    }
    private var localLyricsControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Use Lyrimuse’s fetched lyrics").font(AppFont.ui(13, .semibold))
            Text("Lyrimuse can fetch from its enabled sources, including services this app doesn’t contact directly. Choose its lyrics folder to read matching LRC, TTML, YRC and .roma.lrc files. Lyrimuse must fetch a song first; selecting this folder doesn’t add its online adapters here.")
                .font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.55)).fixedSize(horizontal: false, vertical: true)
            if !local.folder.isEmpty {
                Text(local.folder).font(AppFont.ui(10)).foregroundStyle(.white.opacity(0.45)).textSelection(.enabled)
            }
            HStack {
                Button("Choose lyrics folder…") { chooseLocalFolder() }
                Button("Use Lyrimuse default") { useLocalFolder(local.defaultFolder) }
                if !local.folder.isEmpty {
                    Button("Check for new files") { model.refresh(force: true) }
                        .disabled(model.currentQuery == nil || model.isLoading)
                }
            }.font(AppFont.ui(10.5))
            if let importError { Text(importError).font(AppFont.ui(10.5)).foregroundStyle(.orange) }
        }
        .padding(16).background(RoundedRectangle(cornerRadius: 12).fill(.white.opacity(0.045)))
    }
    private func chooseLocalFolder() {
        let panel = NSOpenPanel()
        panel.title = "Choose Lyrimuse’s exported lyrics folder"
        panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        useLocalFolder(url.path)
    }
    private func useLocalFolder(_ path: String) {
        var directory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &directory), directory.boolValue else {
            importError = "That folder doesn’t exist yet. Fetch a song in Lyrimuse, or choose its custom lyrics folder."; return
        }
        importError = nil; local.folder = path; options.enabled.insert(.lyrimuse)
        model.refresh(force: true)
    }
    private func sourceCard(_ source:LyricSourceID)->some View {
        let enabled=options.enabled.contains(source)
        return VStack(alignment:.leading,spacing:6) {
            HStack(spacing:7) {
                Button {
                    if enabled {options.enabled.remove(source)} else {options.enabled.insert(source)}
                } label:{
                    HStack(spacing:7) {
                        Image(systemName:enabled ? "checkmark.circle.fill" : "circle")
                            .foregroundStyle(enabled ? Verso.violet : .white.opacity(0.3))
                        Text(source.title).font(AppFont.ui(11,.medium)).lineLimit(1)
                        Spacer(minLength:0)
                    }
                    .contentShape(Rectangle())
                }.buttonStyle(.plain).help(source.title)
                if options.mode == .priority {
                    Button {options.move(source,by:-1)} label:{Image(systemName:"chevron.up")}.disabled(options.order.first == source)
                    Button {options.move(source,by:1)} label:{Image(systemName:"chevron.down")}.disabled(options.order.last == source)
                }
            }
            Text(source.timing).font(AppFont.ui(9.5)).foregroundStyle(.white.opacity(0.4))
            if let status=model.sourceStates[source] {
                Text(status).font(AppFont.ui(9.5)).foregroundStyle(status=="Match found" ? .mint : .white.opacity(0.5))
                    .lineLimit(2).help(status)
            }
        }
        .padding(11).background(RoundedRectangle(cornerRadius:8).fill(.white.opacity(enabled ? 0.08 : 0.025)))
    }
    private func importFile() {
        let panel=NSOpenPanel()
        panel.allowedContentTypes=[UTType(filenameExtension:"lrc") ?? .plainText,.plainText]
        panel.allowsMultipleSelection=false;panel.canChooseDirectories=false
        panel.title="Import timed lyrics for the playing song"
        guard panel.runModal() == .OK,let url=panel.url else {return}
        do {
            let data=try Data(contentsOf:url)
            guard data.count<=1_000_000 else {importError="Choose a lyric file smaller than 1 MB.";return}
            guard let text=String(data:data,encoding:.utf8) ?? String(data:data,encoding:.utf16) else {
                importError="Save the file as UTF-8 or UTF-16 text.";return
            }
            importError=nil;_=model.importLyrics(text)
        } catch {importError="That lyric file could not be read."}
    }
}
