//// A Site's files, checked against what is declared about the Site. Slice F1
//// of docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started.
////
//// A Site's files are the SSH host entry this machine reaches it by and the
//// systemd unit that keeps its supervisor running, with that unit's drop-in
//// files. Each fact they carry — the port, the Home's address on the Site, the
//// compiler for each implementation, the thread limits — is declared once, in
//// the Site's execution profile and its supervisor declaration, and this flow
//// says, fact by fact, where the files and the configuration systemd has
//// loaded agree with the declaration and where they do not.
////
//// CHG 2026-09-26 (installed sources retired;
//// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): the
//// compiler for each implementation is no longer declared or rendered: no
//// Site holds implementations, and `--fern-compile` names the one compiler a
//// Site runs. `run-study-cells` no longer accepts `--source-compiler`, so the
//// check expects none, and a unit that still passes one is a difference.
////
//// It reads text it is given and decides; it opens no file, runs no command,
//// and never decides to delete a file or stop a unit. The caller reads the
//// files and systemd's state, and writes only what a person has asked for.
////
//// On 2026-09-25 the Beijing RTX 5090 came back on a new port. Its port was
//// in five places, the Home's address on the Site in 34 start commands, the
//// order of Studies in the names of five drop-ins, and the unit that should
//// have served it was restarting on a configuration systemd had loaded before
//// the files were fixed. Each of those is a finding here.

import gleam/dict.{type Dict}
import gleam/dynamic/decode
import gleam/int
import gleam/json
import gleam/list
import gleam/option.{type Option, None, Some}
import gleam/result
import gleam/string

// ── What is declared ─────────────────────────────────────────────────────

/// How this machine reaches the Site: the user, host and port of its
/// `ssh_destination`.
pub type SshAddress {
  SshAddress(user: String, host: String, port: Int)
}

/// The SSH tunnel's remote forward: the port the Home answers on inside the
/// Site, and the port on this machine it is carried to.
pub type RemoteForward {
  RemoteForward(site_port: Int, home_port: Int)
}

/// Everything declared about a Site that its files carry.
pub type Declaration {
  Declaration(
    site: String,
    held_through_ssh: Bool,
    address: Option(SshAddress),
    python: String,
    work_root: String,
    remote_forward: RemoteForward,
    ssh_compression: Bool,
    ssh_control_path: Option(String),
    ssh_command_timeout_seconds: Option(Int),
    /// The runtime binary on the Site that runs the supervisor
    /// (`run-study-cells`). CHG 2026-09-25 (slice F2): declared, so the
    /// rendered unit names it and the check compares it.
    fern_run: String,
    fern_compile: Option(String),
    process_environment: List(#(String, String)),
    cell_timeout_seconds: Option(Int),
    lease_seconds: Option(Int),
    idle_seconds: Option(Int),
    restart: String,
    restart_seconds: Option(Int),
  )
}

/// The alias every tool reaches the Site by.
pub fn alias(site: String) -> String {
  "fern-" <> site
}

/// The Home's address as the Site sees it, derived and never typed.
pub fn home_on_site(forward: RemoteForward) -> String {
  "http://127.0.0.1:" <> int.to_string(forward.site_port) <> "/"
}

/// Read the two declarations. The profile is the execution profile the Site
/// is registered from; only the facts its files carry are read from it. The
/// supervisor declaration is `fern.site_supervisor.v1` and is read strictly:
/// a field it does not know refuses it, so a misspelt fact is not silently
/// left out of the files.
pub fn declare(
  profile_json: String,
  supervisor_json: String,
) -> Result(Declaration, String) {
  use profile <- result.try(
    json.parse(profile_json, profile_decoder())
    |> result.replace_error("the execution profile cannot be read"),
  )
  let #(site, executor, destination, python, work_root) = profile
  use supervisor <- result.try(read_supervisor(supervisor_json))
  use address <- result.try(case executor, destination {
    "ssh", Some(text) -> result.map(ssh_address(text), Some)
    "ssh", None ->
      Error(
        "the execution profile's executor is ssh but it names no ssh_destination",
      )
    _, _ -> Ok(None)
  })
  case supervisor.site == site {
    False ->
      Error(
        "the supervisor declaration is for Site "
        <> supervisor.site
        <> " and the profile for "
        <> site,
      )
    True ->
      Ok(Declaration(
        site: site,
        held_through_ssh: executor == "ssh",
        address: address,
        python: python,
        work_root: work_root,
        remote_forward: supervisor.remote_forward,
        ssh_compression: supervisor.ssh_compression,
        ssh_control_path: supervisor.ssh_control_path,
        ssh_command_timeout_seconds: supervisor.ssh_command_timeout_seconds,
        fern_run: supervisor.fern_run,
        fern_compile: supervisor.fern_compile,
        process_environment: supervisor.process_environment,
        cell_timeout_seconds: supervisor.cell_timeout_seconds,
        lease_seconds: supervisor.lease_seconds,
        idle_seconds: supervisor.idle_seconds,
        restart: supervisor.restart,
        restart_seconds: supervisor.restart_seconds,
      ))
  }
}

fn profile_decoder() -> decode.Decoder(
  #(String, String, Option(String), String, String),
) {
  use site <- decode.field("profile_id", decode.string)
  use executor <- decode.field("executor", decode.string)
  use destination <- decode.optional_field(
    "ssh_destination",
    None,
    decode.optional(decode.string),
  )
  use python <- decode.field("python", decode.string)
  use work_root <- decode.field("work_root", decode.string)
  decode.success(#(site, executor, destination, python, work_root))
}

/// `user@host:port`, or `user@host` for port 22, ssh's own default.
pub fn ssh_address(text: String) -> Result(SshAddress, String) {
  case string.split_once(text, "@") {
    Error(_) ->
      Error(
        "ssh_destination " <> text <> " names no user; it is user@host:port",
      )
    Ok(#(user, rest)) ->
      case string.split(rest, ":") {
        [host] -> Ok(SshAddress(user, host, 22))
        [host, port] ->
          case int.parse(port) {
            Ok(number) -> Ok(SshAddress(user, host, number))
            Error(_) ->
              Error(
                "ssh_destination " <> text <> " has a port that is not a number",
              )
          }
        _ -> Error("ssh_destination " <> text <> " is not user@host:port")
      }
  }
}

type Supervisor {
  Supervisor(
    site: String,
    remote_forward: RemoteForward,
    ssh_compression: Bool,
    ssh_control_path: Option(String),
    ssh_command_timeout_seconds: Option(Int),
    /// The runtime binary on the Site that runs the supervisor
    /// (`run-study-cells`). CHG 2026-09-25 (slice F2): declared, so the
    /// rendered unit names it and the check compares it.
    fern_run: String,
    fern_compile: Option(String),
    process_environment: List(#(String, String)),
    cell_timeout_seconds: Option(Int),
    lease_seconds: Option(Int),
    idle_seconds: Option(Int),
    restart: String,
    restart_seconds: Option(Int),
  )
}

const supervisor_fields = [
  "schema", "site", "remote_forward", "ssh_compression", "ssh_control_path",
  "ssh_command_timeout_seconds", "fern_run", "fern_compile",
  "process_environment", "cell_timeout_seconds", "lease_seconds", "idle_seconds",
  "restart", "restart_seconds",
]

fn read_supervisor(text: String) -> Result(Supervisor, String) {
  use keys <- result.try(
    json.parse(text, decode.dict(decode.string, decode.dynamic))
    |> result.replace_error("the supervisor declaration is not a JSON object"),
  )
  let unknown =
    dict.keys(keys)
    |> list.filter(fn(key) { !list.contains(supervisor_fields, key) })
    |> list.sort(string.compare)
  case unknown {
    [_, ..] ->
      Error(
        "the supervisor declaration names fields it does not have: "
        <> string.join(unknown, ", "),
      )
    [] ->
      json.parse(text, supervisor_decoder())
      |> result.map_error(fn(error) {
        case error {
          // A required field that is absent is reported as expecting "Field".
          json.UnableToDecode([decode.DecodeError("Field", _, path), ..]) ->
            "the supervisor declaration names no " <> string.join(path, ".")
          json.UnableToDecode([decode.DecodeError(expected, _, path), ..]) ->
            "the supervisor declaration's "
            <> string.join(path, ".")
            <> " is not "
            <> expected
          _ -> "the supervisor declaration cannot be read"
        }
      })
  }
}

fn supervisor_decoder() -> decode.Decoder(Supervisor) {
  use schema <- decode.field("schema", decode.string)
  use site <- decode.field("site", decode.string)
  use forward <- decode.field("remote_forward", {
    use site_port <- decode.field("site_port", decode.int)
    use home_port <- decode.field("home_port", decode.int)
    decode.success(RemoteForward(site_port, home_port))
  })
  use compression <- decode.field("ssh_compression", decode.bool)
  use control <- decode.optional_field(
    "ssh_control_path",
    None,
    decode.optional(decode.string),
  )
  use timeout <- decode.optional_field(
    "ssh_command_timeout_seconds",
    None,
    decode.optional(decode.int),
  )
  use fern_run <- decode.field("fern_run", decode.string)
  use compile <- decode.optional_field(
    "fern_compile",
    None,
    decode.optional(decode.string),
  )
  use environment <- decode.field(
    "process_environment",
    decode.dict(decode.string, decode.string),
  )
  use cell_timeout <- decode.optional_field(
    "cell_timeout_seconds",
    None,
    decode.optional(decode.int),
  )
  use lease <- decode.optional_field(
    "lease_seconds",
    None,
    decode.optional(decode.int),
  )
  use idle <- decode.optional_field(
    "idle_seconds",
    None,
    decode.optional(decode.int),
  )
  use restart <- decode.field("restart", decode.string)
  use restart_seconds <- decode.optional_field(
    "restart_seconds",
    None,
    decode.optional(decode.int),
  )
  case schema {
    "fern.site_supervisor.v1" ->
      decode.success(Supervisor(
        site,
        forward,
        compression,
        control,
        timeout,
        fern_run,
        compile,
        environment
          |> dict.to_list
          |> list.sort(fn(a, b) { string.compare(a.0, b.0) }),
        cell_timeout,
        lease,
        idle,
        restart,
        restart_seconds,
      ))
    _ ->
      decode.failure(
        Supervisor(
          site,
          forward,
          compression,
          None,
          None,
          fern_run,
          None,
          [],
          None,
          None,
          None,
          restart,
          None,
        ),
        "fern.site_supervisor.v1",
      )
  }
}

// ── What is found ────────────────────────────────────────────────────────

/// One file as the caller read it: its path and its text.
pub type FileText {
  FileText(path: String, text: String)
}

/// A unit as systemd has loaded it: from a file or created at run time, its
/// state, whether its files changed since it was loaded, and the start
/// commands and environment it would use now.
pub type LoadedUnit {
  LoadedUnit(
    name: String,
    temporary: Bool,
    active_state: String,
    sub_state: String,
    need_daemon_reload: Bool,
    exec_start: List(List(String)),
    environment: List(#(String, String)),
    /// How many times systemd restarted it by itself since a person last
    /// started it (`NRestarts`). CHG 2026-09-25: one reading of the state is
    /// an instant, and a unit in a restart loop reads as running part of the
    /// time; this count does not.
    restarts: Int,
  )
}

/// Everything the caller read for one Site.
pub type Observed {
  Observed(
    ssh_include: Option(FileText),
    ssh_config: Option(FileText),
    unit: Option(FileText),
    drop_ins: List(FileText),
    loaded: List(LoadedUnit),
  )
}

/// The settings a unit's files produce together, applying systemd's rules
/// for the keys a supervisor unit uses: an empty `ExecStart=`,
/// `ExecStartPre=` or `Environment=` clears its list; a later `Environment=`
/// of the same name wins; words may be quoted; a start command's `-@:+!`
/// prefixes are not words; and the last value of any other key wins.
///
/// What it does not do, said so it is not trusted for it: it expands no
/// specifier but `%%` (a literal `%`), reads no line continuation, keeps one
/// table for all sections, and treats list keys other than the three above
/// (such as `ConditionPathExists=`) as single values. None of those change the
/// facts this flow compares in the units written today.
pub type Effective {
  Effective(
    exec_start: List(List(String)),
    exec_start_pre: List(List(String)),
    environment: List(#(String, String)),
    single: Dict(String, String),
  )
}

pub fn effective(files: List(FileText)) -> Effective {
  list.fold(files, Effective([], [], [], dict.new()), fn(settings, file) {
    list.fold(assignments(file.text), settings, fn(settings, pair) {
      let #(key, value) = pair
      case key, value {
        "ExecStart", "" -> Effective(..settings, exec_start: [])
        "ExecStart", _ ->
          Effective(
            ..settings,
            exec_start: list.append(settings.exec_start, [command(value)]),
          )
        "ExecStartPre", "" -> Effective(..settings, exec_start_pre: [])
        "ExecStartPre", _ ->
          Effective(
            ..settings,
            exec_start_pre: list.append(settings.exec_start_pre, [
              command(value),
            ]),
          )
        "Environment", "" -> Effective(..settings, environment: [])
        "Environment", _ ->
          Effective(
            ..settings,
            environment: list.fold(
              words(value),
              settings.environment,
              fn(environment, word) {
                case string.split_once(word, "=") {
                  Ok(#(name, set)) -> [
                    #(name, unescape(set)),
                    ..list.filter(environment, fn(pair) { pair.0 != name })
                  ]
                  Error(_) -> environment
                }
              },
            ),
          )
        _, _ ->
          Effective(
            ..settings,
            single: dict.insert(settings.single, key, value),
          )
      }
    })
  })
}

/// `Key=Value` lines, without comments, section headers or blank lines.
fn assignments(text: String) -> List(#(String, String)) {
  text
  |> string.split("\n")
  |> list.filter_map(fn(line) {
    let line = string.trim(line)
    case line {
      "" | "#" <> _ | ";" <> _ | "[" <> _ -> Error(Nil)
      _ ->
        case string.split_once(line, "=") {
          Ok(#(key, value)) -> Ok(#(string.trim(key), string.trim(value)))
          Error(_) -> Error(Nil)
        }
    }
  })
}

/// A start command's words. Its prefixes (`-` ignore failure, `@`, `:`, `+`,
/// `!`, `!!`) are not words.
fn command(value: String) -> List(String) {
  words(strip_prefixes(value)) |> list.map(unescape)
}

fn strip_prefixes(value: String) -> String {
  case value {
    "-" <> rest | "@" <> rest | ":" <> rest | "+" <> rest | "!" <> rest ->
      strip_prefixes(rest)
    _ -> value
  }
}

/// Words separated by spaces or tabs; a word in double or single quotes may
/// hold spaces, and loses its quotes.
pub fn words(text: String) -> List(String) {
  let #(current, found, _, started) =
    list.fold(
      string.to_graphemes(text),
      #("", [], None, False),
      fn(state, character) {
        let #(current, found, quote, started) = state
        case quote, character {
          Some(open), _ if character == open -> #(current, found, None, True)
          Some(_), _ -> #(current <> character, found, quote, True)
          None, "\"" | None, "'" -> #(current, found, Some(character), True)
          None, " " | None, "\t" ->
            case started {
              True -> #("", [current, ..found], None, False)
              False -> state
            }
          None, _ -> #(current <> character, found, None, True)
        }
      },
    )
  let found = case started {
    True -> [current, ..found]
    False -> found
  }
  list.reverse(found)
}

/// systemd writes a literal `%` as `%%`.
fn unescape(text: String) -> String {
  string.replace(text, "%%", "%")
}

fn escape(text: String) -> String {
  string.replace(text, "%", "%%")
}

// ── What differs ─────────────────────────────────────────────────────────

/// What a file the flow did not render changes about the unit.
pub type Effect {
  ReplacesStartCommand
  CarriesStudies(count: Int)
  /// Commands run before the supervisor starts, and how many name Studies.
  RunsBeforeStart(count: Int, naming_studies: Int)
  MovesOutputAwayFromJournal(target: String)
  SetsRestart(policy: String)
  SetsEnvironment(names: List(String))
}

pub type Finding {
  /// The declarations cannot be read; nothing else is checked.
  Undeclared(detail: String)
  /// The file is what the declaration renders, fact for fact.
  Matches(path: String)
  Missing(path: String)
  /// One fact, as declared and as found in the file (or in the files of a
  /// unit together).
  Differs(path: String, fact: String, declared: String, found: String)
  /// A file in the unit's drop-in directory that the flow does not render.
  Extra(path: String, effects: List(Effect))
  /// A `Host` block for the Site's alias kept by hand in `~/.ssh/config`,
  /// which ssh reads before, or instead of, the rendered one.
  KeptByHand(path: String, alias: String)
  /// systemd is running a configuration older than the files: a reload would
  /// change what it does.
  LoadedOlder(unit: String, fact: String, loaded: String, file: String)
  /// Another loaded unit starts a supervisor for the same Site.
  AlsoServes(unit: String, temporary: Bool, state: String)
  /// Another unit serving the Site runs a command whose fact is not the
  /// declared one: what it trains with is not what is declared.
  LoadedDiffers(unit: String, fact: String, declared: String, found: String)
  /// The Site's own unit is not running: restarting, starting or failed.
  NotRunning(unit: String, state: String)
  /// The Site's own unit was restarted by systemd this many times since a
  /// person last started it.
  Restarted(unit: String, times: Int)
  /// `~/.ssh/config` does not read the rendered entries before its first
  /// `Host` block, so ssh would not use them.
  NotIncluded(path: String)
}

/// Every finding for one Site, in the order a person should read them.
pub fn check(
  declared: Result(Declaration, String),
  observed: Observed,
  unit_name: String,
) -> List(Finding) {
  case declared {
    Error(detail) -> [Undeclared(detail)]
    Ok(declaration) ->
      list.flatten([
        ssh_findings(declaration, observed),
        unit_findings(declaration, observed),
        loaded_findings(declaration, observed, unit_name),
      ])
  }
}

pub fn unit_name(site: String) -> String {
  "fern-study-supervisor-" <> site <> ".service"
}

fn ssh_findings(declaration: Declaration, observed: Observed) -> List(Finding) {
  case declaration.address {
    None -> []
    Some(address) -> {
      let alias = alias(declaration.site)
      let rendered = case observed.ssh_include {
        None -> [Missing("~/.ssh/fern-sites.config")]
        Some(file) ->
          case host_block(file.text, alias) {
            None -> [Missing(file.path <> " (Host " <> alias <> ")")]
            Some(block) ->
              case ssh_differences(declaration, address, block, file.path) {
                [] -> [Matches(file.path)]
                differences -> differences
              }
          }
      }
      let by_hand = case observed.ssh_config {
        None -> []
        Some(file) ->
          case names_literally(file.text, alias), host_block(file.text, alias) {
            True, Some(block) -> [
              KeptByHand(file.path, alias),
              ..ssh_differences(declaration, address, block, file.path)
            ]
            _, _ -> []
          }
      }
      let included = case observed.ssh_config {
        None -> [NotIncluded("~/.ssh/config")]
        Some(file) ->
          case includes_rendered_first(file.text) {
            True -> []
            False -> [NotIncluded(file.path)]
          }
      }
      list.flatten([by_hand, included, rendered])
    }
  }
}

fn ssh_differences(
  declaration: Declaration,
  address: SshAddress,
  block: Dict(String, String),
  path: String,
) -> List(Finding) {
  let forward =
    "127.0.0.1:"
    <> int.to_string(declaration.remote_forward.site_port)
    <> " 127.0.0.1:"
    <> int.to_string(declaration.remote_forward.home_port)
  [
    #("HostName", address.host),
    #("User", address.user),
    #("Port", int.to_string(address.port)),
    #("RemoteForward", forward),
    #("ExitOnForwardFailure", "yes"),
    #("Compression", case declaration.ssh_compression {
      True -> "yes"
      False -> "no"
    }),
  ]
  |> list.filter_map(fn(pair) {
    let #(option, declared) = pair
    let found =
      dict.get(block, string.lowercase(option))
      |> result.unwrap(default_ssh(option))
    case found == declared {
      True -> Error(Nil)
      False -> Ok(Differs(path, "ssh " <> option, declared, found))
    }
  })
}

/// ssh's own value for an option a block leaves out.
fn default_ssh(option: String) -> String {
  case option {
    "Port" -> "22"
    "Compression" | "ExitOnForwardFailure" -> "no"
    _ -> "(not set)"
  }
}

/// The options ssh would use for the alias from this text: across every
/// `Host` block whose patterns match it (`*` and `?` wildcards, `!` to
/// exclude), and before the first `Host` line, which applies to every host,
/// the first value of each option, by lower-cased name. `Option value` and
/// `Option=value` are both read. `Match` blocks are skipped: this does not
/// evaluate them. `None` when no `Host` block matches.
fn host_block(text: String, alias: String) -> Option(Dict(String, String)) {
  // Options before the first `Host` or `Match` line apply to every host.
  let #(block, _, matched) =
    list.fold(ssh_lines(text), #(dict.new(), True, False), fn(state, line) {
      let #(block, inside, matched) = state
      let #(keyword, arguments) = line
      case keyword {
        "host" ->
          case host_matches(arguments, alias) {
            True -> #(block, True, True)
            False -> #(block, False, matched)
          }
        "match" -> #(block, False, matched)
        option ->
          case inside && !dict.has_key(block, option) {
            True -> #(
              dict.insert(block, option, string.join(arguments, " ")),
              inside,
              matched,
            )
            False -> state
          }
      }
    })
  case matched {
    True -> Some(block)
    False -> None
  }
}

/// Whether a `Host` line names the alias itself, not only through a pattern.
fn names_literally(text: String, alias: String) -> Bool {
  list.any(ssh_lines(text), fn(line) {
    line.0 == "host" && list.contains(line.1, alias)
  })
}

/// Whether an `Include` naming `fern-sites.config` comes before the first
/// `Host` or `Match` line, where ssh reads it for every host.
fn includes_rendered_first(text: String) -> Bool {
  let before =
    list.take_while(ssh_lines(text), fn(line) {
      line.0 != "host" && line.0 != "match"
    })
  list.any(before, fn(line) {
    line.0 == "include"
    && list.any(line.1, fn(path) { string.ends_with(path, "fern-sites.config") })
  })
}

/// Each non-comment line as a lower-cased keyword and its arguments.
fn ssh_lines(text: String) -> List(#(String, List(String))) {
  text
  |> string.split("\n")
  |> list.filter_map(fn(line) {
    let line = string.trim(string.replace(line, "\t", " "))
    case line {
      "" | "#" <> _ -> Error(Nil)
      _ -> {
        let #(keyword, rest) = case string.split_once(line, " ") {
          Ok(#(keyword, rest)) -> #(keyword, rest)
          Error(_) -> #(line, "")
        }
        let #(keyword, rest) = case string.split_once(keyword, "=") {
          Ok(#(name, value)) -> #(name, value <> " " <> rest)
          Error(_) -> #(keyword, rest)
        }
        let rest = case string.trim(rest) {
          "=" <> value -> value
          value -> value
        }
        Ok(#(string.lowercase(keyword), words(rest)))
      }
    }
  })
}

fn host_matches(patterns: List(String), alias: String) -> Bool {
  let #(negated, plain) =
    list.partition(patterns, fn(pattern) { string.starts_with(pattern, "!") })
  !list.any(negated, fn(pattern) { glob(string.drop_start(pattern, 1), alias) })
  && list.any(plain, fn(pattern) { glob(pattern, alias) })
}

/// ssh's pattern: `*` any run of characters, `?` one character.
fn glob(pattern: String, text: String) -> Bool {
  glob_graphemes(string.to_graphemes(pattern), string.to_graphemes(text))
}

fn glob_graphemes(pattern: List(String), text: List(String)) -> Bool {
  case pattern, text {
    [], [] -> True
    ["*", ..rest], _ ->
      glob_graphemes(rest, text)
      || case text {
        [_, ..more] -> glob_graphemes(pattern, more)
        [] -> False
      }
    ["?", ..rest], [_, ..more] -> glob_graphemes(rest, more)
    [expected, ..rest], [character, ..more] if expected == character ->
      glob_graphemes(rest, more)
    _, _ -> False
  }
}

fn unit_findings(
  declaration: Declaration,
  observed: Observed,
) -> List(Finding) {
  let name = unit_name(declaration.site)
  case observed.unit {
    None -> [Missing("~/.config/systemd/user/" <> name)]
    Some(unit) -> {
      let files = [unit, ..observed.drop_ins]
      let settings = effective(files)
      let extra =
        list.map(observed.drop_ins, fn(file) {
          Extra(file.path, effects(file.text))
        })
      let facts = unit_facts(declaration, settings, True)
      let differing =
        list.filter_map(facts, fn(fact) {
          let #(label, declared, found) = fact
          case declared == found {
            True -> Error(Nil)
            False ->
              Ok(Differs(name <> " with its drop-ins", label, declared, found))
          }
        })
      case differing, extra {
        [], [] -> [Matches(unit.path)]
        _, _ -> list.append(differing, extra)
      }
    }
  }
}

/// What a drop-in changes, read from its own lines.
fn effects(text: String) -> List(Effect) {
  let pairs = assignments(text)
  let starts =
    list.filter(pairs, fn(pair) { pair.0 == "ExecStart" && pair.1 != "" })
  let studies =
    list.count(starts, fn(pair) { list.contains(command(pair.1), "--study") })
  let replaces = case list.any(pairs, fn(pair) { pair.0 == "ExecStart" }) {
    True -> [ReplacesStartCommand]
    False -> []
  }
  let carries = case studies {
    0 -> []
    n -> [CarriesStudies(n)]
  }
  let before =
    list.filter(pairs, fn(pair) { pair.0 == "ExecStartPre" && pair.1 != "" })
  let runs_before = case before {
    [] -> []
    _ -> [
      RunsBeforeStart(
        list.length(before),
        list.count(before, fn(pair) {
          list.contains(command(pair.1), "--study")
        }),
      ),
    ]
  }
  let output =
    list.filter_map(pairs, fn(pair) {
      case pair.0 {
        "StandardOutput" | "StandardError" ->
          case pair.1 {
            "journal" | "inherit" -> Error(Nil)
            target -> Ok(target)
          }
        _ -> Error(Nil)
      }
    })
    |> list.unique
    |> list.map(MovesOutputAwayFromJournal)
  let restart =
    list.filter_map(pairs, fn(pair) {
      case pair.0 {
        "Restart" -> Ok(SetsRestart(pair.1))
        _ -> Error(Nil)
      }
    })
  let environment =
    list.flat_map(pairs, fn(pair) {
      case pair.0 {
        "Environment" ->
          string.split(pair.1, " ")
          |> list.filter_map(fn(word) {
            string.split_once(word, "=") |> result.map(fn(split) { split.0 })
          })
        _ -> []
      }
    })
  let sets = case environment {
    [] -> []
    names -> [SetsEnvironment(list.unique(names))]
  }
  list.flatten([replaces, carries, runs_before, output, restart, sets])
}

/// Each fact the unit carries: its label, its declared value and the value
/// the unit's files produce together.
fn unit_facts(
  declaration: Declaration,
  settings: Effective,
  with_unit_settings: Bool,
) -> List(#(String, String, String)) {
  let start = case settings.exec_start {
    [only] -> only
    _ -> []
  }
  let arguments = run_study_cells_arguments(start)
  let target = find_environment(settings.environment, "RUNPOD_SSH_TARGET")
  let declared_target = case declaration.address {
    Some(address) ->
      alias(declaration.site) <> ":" <> int.to_string(address.port)
    None -> "(not held through SSH)"
  }
  let process =
    process_environment(start)
    |> list.sort(fn(a, b) { string.compare(a.0, b.0) })
    |> list.map(fn(pair) { pair.0 <> "=" <> pair.1 })
    |> string.join(" ")
  let declared_process =
    declaration.process_environment
    |> list.map(fn(pair) { pair.0 <> "=" <> pair.1 })
    |> string.join(" ")
  let output =
    [
      dict.get(settings.single, "StandardOutput"),
      dict.get(settings.single, "StandardError"),
    ]
    |> list.filter_map(fn(found) { found })
    |> list.filter(fn(found) { found != "journal" && found != "inherit" })
  let facts = [
    #("start commands", "1", int.to_string(list.length(settings.exec_start))),
    #("RUNPOD_SSH_TARGET", declared_target, target),
    #(
      "RUNPOD_COMMAND_TIMEOUT_SECONDS",
      optional_int(declaration.ssh_command_timeout_seconds),
      find_environment(settings.environment, "RUNPOD_COMMAND_TIMEOUT_SECONDS"),
    ),
    #(
      "RUNPOD_CONTROL_PATH",
      option.unwrap(declaration.ssh_control_path, "(not set)"),
      find_environment(settings.environment, "RUNPOD_CONTROL_PATH"),
    ),
    #("runtime binary on the Site", declaration.fern_run, runtime_binary(start)),
    #("--fern-run", declaration.fern_run, first_value(arguments, "--fern-run")),
    #("--site", declaration.site, first_value(arguments, "--site")),
    #(
      "--home",
      home_on_site(declaration.remote_forward),
      first_value(arguments, "--home"),
    ),
    #("--python", declaration.python, first_value(arguments, "--python")),
    #(
      "--work-root",
      declaration.work_root,
      first_value(arguments, "--work-root"),
    ),
    #(
      "--fern-compile",
      option.unwrap(declaration.fern_compile, "(not set)"),
      first_value(arguments, "--fern-compile"),
    ),
    // CHG 2026-09-26 (installed sources retired;
    // docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4):
    // none is expected, as no `--study` is; a unit that still passes one does
    // not start, and `update-site-files` renders the unit without it.
    #(
      retired_source_compiler,
      "(none)",
      case option_values(arguments, retired_source_compiler) {
        [] -> "(none)"
        pairs -> string.join(pairs, ", ")
      },
    ),
    #(
      "--cell-timeout-seconds",
      optional_int(declaration.cell_timeout_seconds),
      first_value(arguments, "--cell-timeout-seconds"),
    ),
    #(
      "--lease-seconds",
      optional_int(declaration.lease_seconds),
      first_value(arguments, "--lease-seconds"),
    ),
    #(
      "--idle-seconds",
      optional_int(declaration.idle_seconds),
      first_value(arguments, "--idle-seconds"),
    ),
    #("--study", "(none)", case option_values(arguments, "--study") {
      [] -> "(none)"
      studies -> string.join(studies, ", ")
    }),
    #("process environment", declared_process, process),
    #(
      "Restart",
      declaration.restart,
      dict.get(settings.single, "Restart") |> result.unwrap("(not set)"),
    ),
    #(
      "RestartSec",
      case declaration.restart_seconds {
        Some(seconds) -> int.to_string(seconds) <> "s"
        None -> "(not set)"
      },
      dict.get(settings.single, "RestartSec") |> result.unwrap("(not set)"),
    ),
    #("output", "journal", case output {
      [] -> "journal"
      targets -> string.join(list.unique(targets), ", ")
    }),
  ]
  case with_unit_settings {
    True -> facts
    // A loaded unit is read for its command and environment only.
    False ->
      list.filter(facts, fn(fact) {
        !list.contains(
          ["start commands", "Restart", "RestartSec", "output"],
          fact.0,
        )
      })
  }
}

fn loaded_findings(
  declaration: Declaration,
  observed: Observed,
  unit_name: String,
) -> List(Finding) {
  let site_argument = declaration.site
  let serving =
    list.filter(observed.loaded, fn(unit) {
      list.any(unit.exec_start, fn(start) {
        first_value(run_study_cells_arguments(start), "--site") == site_argument
      })
    })
  let own = list.find(observed.loaded, fn(unit) { unit.name == unit_name })
  let older = case own, observed.unit {
    Ok(unit), Some(file) if unit.need_daemon_reload -> {
      let from_files = effective([file, ..observed.drop_ins])
      let loaded_target =
        find_environment(unit.environment, "RUNPOD_SSH_TARGET")
      let file_target =
        find_environment(from_files.environment, "RUNPOD_SSH_TARGET")
      let loaded_home = first_home(unit.exec_start)
      let file_home = first_home(from_files.exec_start)
      let differences =
        [
          #("RUNPOD_SSH_TARGET", loaded_target, file_target),
          #("--home", loaded_home, file_home),
        ]
        |> list.filter(fn(fact) { fact.1 != fact.2 })
        |> list.map(fn(fact) { LoadedOlder(unit.name, fact.0, fact.1, fact.2) })
      case differences {
        [] -> [
          LoadedOlder(
            unit.name,
            "files",
            "loaded before the last edit",
            "changed since",
          ),
        ]
        _ -> differences
      }
    }
    _, _ -> []
  }
  let not_running = case own, observed.unit {
    Ok(unit), _
      if unit.active_state != "active" || unit.sub_state != "running"
    -> [
      NotRunning(unit.name, unit.active_state <> "/" <> unit.sub_state),
    ]
    // The file is there but systemd has not loaded it.
    Error(_), Some(_) -> [NotRunning(unit_name, "not loaded by systemd")]
    _, _ -> []
  }
  let restarted = case own {
    Ok(unit) if unit.restarts > 0 -> [Restarted(unit.name, unit.restarts)]
    _ -> []
  }
  let others =
    list.filter(serving, fn(unit) {
      unit.name != unit_name
      && { unit.active_state == "active" || unit.active_state == "activating" }
    })
  let active =
    list.flat_map(others, fn(unit) {
      let starts =
        list.filter(unit.exec_start, fn(start) {
          first_value(run_study_cells_arguments(start), "--site")
          == site_argument
        })
      let facts =
        unit_facts(
          declaration,
          Effective(starts, [], unit.environment, dict.new()),
          False,
        )
        |> list.filter(fn(fact) { fact.1 != fact.2 })
        |> list.map(fn(fact) {
          LoadedDiffers(unit.name, fact.0, fact.1, fact.2)
        })
      [
        AlsoServes(
          unit.name,
          unit.temporary,
          unit.active_state <> "/" <> unit.sub_state,
        ),
        ..facts
      ]
    })
  list.flatten([not_running, restarted, older, active])
}

fn first_home(starts: List(List(String))) -> String {
  case starts {
    [start, ..] -> first_value(run_study_cells_arguments(start), "--home")
    [] -> "(no start command)"
  }
}

/// The words after `run-study-cells` in a start command, wherever the
/// command wraps it (the SSH helper, `env`, a runtime binary path).
/// The word before `run-study-cells` in a start command: the binary that runs it.
fn runtime_binary(start: List(String)) -> String {
  case list.split_while(start, fn(word) { word != "run-study-cells" }) {
    #([_, ..] as before, [_, ..]) ->
      list.last(before) |> result.unwrap("(not found)")
    _ -> "(not found)"
  }
}

fn run_study_cells_arguments(start: List(String)) -> List(String) {
  case list.drop_while(start, fn(word) { word != "run-study-cells" }) {
    [_, ..rest] -> rest
    [] -> []
  }
}

/// `NAME=value` words after `env` and before the runtime binary.
fn process_environment(start: List(String)) -> List(#(String, String)) {
  case list.drop_while(start, fn(word) { word != "env" }) {
    [_, ..rest] ->
      rest
      |> list.take_while(fn(word) { string.contains(word, "=") })
      |> list.filter_map(fn(word) { string.split_once(word, "=") })
    [] -> []
  }
}

fn first_value(arguments: List(String), option: String) -> String {
  case option_values(arguments, option) {
    [value, ..] -> value
    [] -> "(not set)"
  }
}

fn option_values(arguments: List(String), option: String) -> List(String) {
  case arguments {
    [] -> []
    [name, value, ..rest] if name == option -> [
      value,
      ..option_values(rest, option)
    ]
    [_, ..rest] -> option_values(rest, option)
  }
}

fn find_environment(
  environment: List(#(String, String)),
  name: String,
) -> String {
  list.key_find(environment, name) |> result.unwrap("(not set)")
}

fn optional_int(value: Option(Int)) -> String {
  case value {
    Some(number) -> int.to_string(number)
    None -> "(not set)"
  }
}

/// The sentence a person reads for one finding.
/// The option a supervisor's start command passed for each implementation's
/// compiler. CHG 2026-09-26 (installed sources retired;
/// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4):
/// `run-study-cells` no longer accepts it.
const retired_source_compiler = "--source-compiler"

/// Why a unit that still passes `--source-compiler` matters, in the words a
/// difference is told with.
const will_not_start = "run-study-cells no longer accepts --source-compiler, so a unit that still passes it will not start"

pub fn describe(finding: Finding) -> String {
  case finding {
    Undeclared(detail) -> "not checked: " <> detail
    Matches(path) -> path <> " matches the declaration"
    Missing(path) -> path <> " is missing"
    Differs(path, fact, declared, found) ->
      path
      <> ": "
      <> fact
      <> " is declared as "
      <> declared
      <> " and found as "
      <> found
      <> case fact == retired_source_compiler {
        True ->
          "; "
          <> will_not_start
          <> ", and update-site-files writes the unit without it"
        False -> ""
      }
    Extra(path, effects) ->
      path
      <> " is not rendered from the declaration"
      <> case effects {
        [] -> ""
        _ -> "; it " <> string.join(list.map(effects, effect_words), ", ")
      }
    KeptByHand(path, alias) ->
      path
      <> " keeps its own Host "
      <> alias
      <> " block, which ssh reads before or instead of the rendered one"
    LoadedOlder(unit, fact, loaded, file) ->
      "systemd runs "
      <> unit
      <> " with "
      <> fact
      <> " "
      <> loaded
      <> ", but its files now say "
      <> file
      <> "; a reload would change what it runs"
    LoadedDiffers(unit, fact, declared, found) ->
      unit
      <> " runs with "
      <> fact
      <> " "
      <> found
      <> ", where the declaration says "
      <> declared
      <> case fact == retired_source_compiler {
        True -> "; " <> will_not_start
        False -> ""
      }
    NotRunning(unit, state) -> unit <> " is " <> state <> ", not running"
    Restarted(unit, times) ->
      unit
      <> " was restarted by systemd "
      <> int.to_string(times)
      <> " time(s) since a person last started it"
    NotIncluded(path) ->
      path
      <> " does not include ~/.ssh/fern-sites.config before its first Host block, so ssh does not read the rendered entries"
    AlsoServes(unit, temporary, state) ->
      unit
      <> case temporary {
        True -> " (created at run time)"
        False -> ""
      }
      <> " also starts a supervisor for this Site and is "
      <> state
  }
}

fn effect_words(effect: Effect) -> String {
  case effect {
    ReplacesStartCommand -> "replaces the start command"
    CarriesStudies(count) ->
      "names Studies in " <> int.to_string(count) <> " start command(s)"
    MovesOutputAwayFromJournal(target) ->
      "sends output to " <> target <> " instead of the journal"
    SetsRestart(policy) -> "sets Restart=" <> policy
    RunsBeforeStart(count, naming) ->
      "runs "
      <> int.to_string(count)
      <> " command(s) before the start"
      <> case naming {
        0 -> ""
        _ -> ", " <> int.to_string(naming) <> " of them naming Studies"
      }
    SetsEnvironment(names) -> "sets " <> string.join(names, ", ")
  }
}

pub fn kind(finding: Finding) -> String {
  case finding {
    Undeclared(_) -> "undeclared"
    Matches(_) -> "matches"
    Missing(_) -> "missing"
    Differs(_, _, _, _) -> "differs"
    Extra(_, _) -> "extra"
    KeptByHand(_, _) -> "kept_by_hand"
    LoadedOlder(_, _, _, _) -> "loaded_older"
    AlsoServes(_, _, _) -> "also_serves"
    LoadedDiffers(_, _, _, _) -> "loaded_differs"
    NotRunning(_, _) -> "not_running"
    Restarted(_, _) -> "restarted"
    NotIncluded(_) -> "not_included"
  }
}

// ── What is rendered ─────────────────────────────────────────────────────

/// The Site's SSH host entry, as `~/.ssh/fern-sites.config` holds it.
pub fn render_ssh_entry(
  declaration: Declaration,
  provenance: String,
) -> Option(String) {
  use address <- option.map(declaration.address)
  "# "
  <> provenance
  <> "\nHost "
  <> alias(declaration.site)
  <> "\n    HostName "
  <> address.host
  <> "\n    User "
  <> address.user
  <> "\n    Port "
  <> int.to_string(address.port)
  <> case declaration.ssh_compression {
    True -> "\n    Compression yes"
    False -> ""
  }
  <> "\n    RemoteForward 127.0.0.1:"
  <> int.to_string(declaration.remote_forward.site_port)
  <> " 127.0.0.1:"
  <> int.to_string(declaration.remote_forward.home_port)
  <> "\n    ExitOnForwardFailure yes\n"
}

/// What the unit needs that the declarations do not hold: the program that
/// opens the SSH session on this machine, the directory the unit runs in, and
/// the checks the unit runs before it starts. CHG 2026-09-25 (slice F2): the
/// runtime binary on the Site is declared (`fern_run`), no longer an input.
pub type UnitInputs {
  UnitInputs(
    /// The unit's PATH. CHG 2026-09-25: systemd's own PATH lacks the
    /// directory `erl` is in, and the checks before start run the Gleam
    /// program, which starts `erl`.
    path: String,
    session_program: String,
    working_directory: String,
    checks_before_start: List(List(String)),
    provenance: String,
  )
}

/// The supervisor unit held from this machine through SSH: one start
/// command, no Study, output in the journal.
pub fn render_unit(
  declaration: Declaration,
  inputs: UnitInputs,
) -> Option(String) {
  use address <- option.map(declaration.address)
  let environment =
    [
      Some(#("PATH", inputs.path)),
      Some(#(
        "RUNPOD_SSH_TARGET",
        alias(declaration.site) <> ":" <> int.to_string(address.port),
      )),
      option.map(declaration.ssh_command_timeout_seconds, fn(seconds) {
        #("RUNPOD_COMMAND_TIMEOUT_SECONDS", int.to_string(seconds))
      }),
      option.map(declaration.ssh_control_path, fn(path) {
        #("RUNPOD_CONTROL_PATH", path)
      }),
    ]
    |> option.values
  let arguments =
    list.flatten([
      [
        declaration.fern_run,
        "run-study-cells",
        "--serve",
        "--home",
        home_on_site(declaration.remote_forward),
        "--site",
        declaration.site,
        "--python",
        declaration.python,
        "--fern-run",
        declaration.fern_run,
      ],
      option.map(declaration.fern_compile, fn(path) { ["--fern-compile", path] })
        |> option.unwrap([]),
      ["--work-root", declaration.work_root],
      numbered("--cell-timeout-seconds", declaration.cell_timeout_seconds),
      numbered("--lease-seconds", declaration.lease_seconds),
      numbered("--idle-seconds", declaration.idle_seconds),
    ])
  let start =
    list.flatten([
      [inputs.session_program, "stdio", "--", "env"],
      list.map(declaration.process_environment, fn(pair) {
        pair.0 <> "=" <> pair.1
      }),
      arguments,
    ])
  "# "
  <> inputs.provenance
  <> "\n[Unit]\nDescription=Fern study supervisor for Site "
  <> declaration.site
  <> ", held from this machine through SSH\n\n[Service]\nType=simple\nWorkingDirectory="
  <> inputs.working_directory
  <> "\n"
  <> string.concat(
    list.map(environment, fn(pair) {
      "Environment=" <> pair.0 <> "=" <> escape(pair.1) <> "\n"
    }),
  )
  <> string.concat(
    list.map(inputs.checks_before_start, fn(check) {
      "ExecStartPre=" <> escape(string.join(check, " ")) <> "\n"
    }),
  )
  <> "ExecStart="
  <> escape(string.join(start, " "))
  <> "\nRestart="
  <> declaration.restart
  <> "\n"
  <> case declaration.restart_seconds {
    Some(seconds) -> "RestartSec=" <> int.to_string(seconds) <> "s\n"
    None -> ""
  }
  <> "RestartPreventExitStatus=2\n\n[Install]\nWantedBy=default.target\n"
}

// ── Writing the supervisor unit ──────────────────────────────────────────

/// What `update-site-files --site` does to the Site's unit: the rendered text,
/// whether to write it, the drop-ins to remove, and whether systemd must
/// reload. Nothing here starts, stops or restarts a unit.
pub type UnitUpdate {
  UnitUpdate(
    text: String,
    write: Bool,
    remove: List(String),
    reload: Bool,
    /// Each fact the update changes: its name, what the unit runs with now
    /// (the file and its drop-ins together), and what it runs with after.
    changes: List(#(String, String, String)),
  )
}

/// Decide the unit's update, or refuse with the sentence a person reads.
///
/// It refuses unless the Site's own unit is stopped (not loaded, inactive or
/// failed): a reload does not start a stopped unit, while a unit that runs or
/// restarts by itself would run the new configuration at its next restart,
/// which would be this update starting a supervisor. It refuses while another
/// unit serving the Site is active. It refuses while drop-ins remain and
/// `remove_extra` is not given, because the rendered unit's own start check
/// refuses to start beside them. And it refuses to move drop-ins aside when
/// what the unit runs with now carries a source-compiler pair or a process
/// environment variable the declaration lacks: moving them would silently
/// drop it. In every refusal nothing is written. CHG 2026-09-25: the
/// stopped-unit and lost-fact refusals follow the reader's second review.
/// CHG 2026-09-26 (installed sources retired;
/// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): a
/// source-compiler pair is no longer a fact the declaration can carry, and
/// moving it aside loses nothing, since `run-study-cells` refuses it; only a
/// process environment variable is a lost fact now.
pub fn update_unit(
  declaration: Declaration,
  observed: Observed,
  inputs: UnitInputs,
  remove_extra: Bool,
) -> Result(UnitUpdate, String) {
  let site = "Site " <> declaration.site
  let name = unit_name(declaration.site)
  let others =
    list.filter(observed.loaded, fn(unit) {
      unit.name != name
      && { unit.active_state == "active" || unit.active_state == "activating" }
      && list.any(unit.exec_start, fn(start) {
        first_value(run_study_cells_arguments(start), "--site")
        == declaration.site
      })
    })
  let own_running =
    list.find(observed.loaded, fn(unit) {
      unit.name == name
      && unit.active_state != "inactive"
      && unit.active_state != "failed"
    })
  let lost = case observed.unit, observed.drop_ins {
    Some(file), [_, ..] -> lost_facts(declaration, [file, ..observed.drop_ins])
    None, [_, ..] -> lost_facts(declaration, observed.drop_ins)
    _, [] -> []
  }
  case render_unit(declaration, inputs), others, own_running, lost {
    None, _, _, _ ->
      Error(
        site
        <> " is not held from this machine through SSH; its unit is written by install-supervisor-service",
      )
    _, [_, ..], _, _ ->
      Error(
        site
        <> " is served by "
        <> string.join(
          list.map(others, fn(unit) {
            unit.name
            <> " ("
            <> case unit.temporary {
              True -> "created at run time, "
              False -> ""
            }
            <> unit.active_state
            <> "/"
            <> unit.sub_state
            <> ")"
          }),
          ", ",
        )
        <> "; nothing is written, because a reload could then start a second supervisor. Stop that unit between two cells first, so exactly one supervisor holds the Site",
      )
    _, [], Ok(unit), _ ->
      Error(
        site
        <> ": "
        <> unit.name
        <> " is "
        <> unit.active_state
        <> "/"
        <> unit.sub_state
        <> case unit.restarts {
          0 -> ""
          times -> ", restarted " <> int.to_string(times) <> " time(s)"
        }
        <> "; nothing is written, because its next start, by itself, would run the new configuration. Stop it between two cells first; this update never starts a unit",
      )
    _, [], Error(_), [_, ..] ->
      Error(
        site
        <> ": what the unit runs with now carries "
        <> string.join(lost, "; ")
        <> ", which the supervisor declaration lacks; nothing is written, because moving the drop-ins aside would drop it. Add it to config/profiles/"
        <> declaration.site
        <> ".supervisor.json first, or say in the declaration that it goes",
      )
    Some(text), [], Error(_), [] ->
      case observed.drop_ins, remove_extra {
        [_, ..], False ->
          Error(
            site
            <> ": "
            <> int.to_string(list.length(observed.drop_ins))
            <> " drop-in(s) change the unit ("
            <> string.join(
              list.map(observed.drop_ins, fn(file) { file.path }),
              ", ",
            )
            <> "); nothing is written, because the rendered unit checks its files before it starts and would refuse to start beside them. Run again with --remove-extra to replace them",
          )
        drop_ins, _ -> {
          let write = case observed.unit {
            None -> True
            Some(file) ->
              without_provenance(file.text) != without_provenance(text)
          }
          let remove = list.map(drop_ins, fn(file) { file.path })
          let changes = case observed.unit {
            None -> []
            Some(file) ->
              unit_facts(declaration, effective([file, ..drop_ins]), True)
              |> list.filter(fn(fact) { fact.1 != fact.2 })
              |> list.map(fn(fact) { #(fact.0, fact.2, fact.1) })
          }
          Ok(UnitUpdate(
            text: text,
            write: write,
            remove: remove,
            reload: write || remove != [],
            changes: changes,
          ))
        }
      }
  }
}

/// What the unit's files run with now that the declaration does not name: a
/// source-compiler pair, or a process environment variable, in the start
/// command in effect.
/// CHG 2026-09-26 (installed sources retired;
/// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): only
/// a process environment variable; source-compiler pairs are not declared.
fn lost_facts(declaration: Declaration, files: List(FileText)) -> List(String) {
  let start = case effective(files).exec_start {
    [only] -> only
    _ -> []
  }
  process_environment(start)
  |> list.filter(fn(pair) {
    !list.contains(declaration.process_environment, pair)
  })
  |> list.map(fn(pair) { "the process environment " <> pair.0 <> "=" <> pair.1 })
}

/// A rendered file's text apart from its first line, which names the binary
/// that wrote it: a newer binary rendering the same unit changes nothing.
fn without_provenance(text: String) -> String {
  case string.split_once(text, "\n") {
    Ok(#("# " <> _, rest)) -> rest
    _ -> text
  }
}

fn numbered(option: String, value: Option(Int)) -> List(String) {
  case value {
    Some(number) -> [option, int.to_string(number)]
    None -> []
  }
}

// ── Writing the SSH entries ──────────────────────────────────────────────

/// What `update-site-files` does to the SSH files: the whole text of
/// `~/.ssh/fern-sites.config`, the new text of `~/.ssh/config`, and what
/// changed in it.
pub type SshUpdate {
  SshUpdate(
    include_text: String,
    config_text: String,
    include_added: Bool,
    removed: List(String),
    kept: List(#(String, String)),
  )
}

pub const include_line = "Include ~/.ssh/fern-sites.config"

/// Render every declared Site's entry into the include file, make
/// `~/.ssh/config` read it before its first `Host` block, and remove a block
/// kept by hand for a declared Site only when it names that Site alone and
/// sets exactly the options, with exactly the values, of the rendered entry:
/// then ssh behaves the same with it gone. Any other hand-kept block is kept,
/// with the reason.
pub fn update_ssh(
  declarations: List(Declaration),
  config: String,
  provenance: String,
) -> SshUpdate {
  let entries =
    list.filter_map(declarations, fn(declaration) {
      render_ssh_entry(declaration, provenance)
      |> option.to_result(Nil)
      |> result.map(fn(text) { #(alias(declaration.site), text) })
    })
  let include_text =
    "# "
    <> provenance
    <> "\n"
    <> string.join(list.map(entries, fn(entry) { entry.1 }), "\n")
  let included = includes_rendered_first(config)
  let #(config, removed, kept) =
    list.fold(entries, #(config, [], []), fn(state, entry) {
      let #(text, removed, kept) = state
      let #(name, rendered) = entry
      case hand_block_lines(text, name) {
        None -> state
        Some(#(from, to, patterns)) ->
          case
            patterns == [name],
            host_block(rendered, name),
            host_block(text, name)
          {
            False, _, _ -> #(text, removed, [
              #(name, "its Host line also names other hosts"),
              ..kept
            ])
            True, Some(wanted), Some(_) ->
              case same_options(wanted, block_options(text, from, to)) {
                True -> #(drop_lines(text, from, to), [name, ..removed], kept)
                False -> #(text, removed, [
                  #(
                    name,
                    "its options differ from the declaration's: "
                      <> describe_options(block_options(text, from, to))
                      <> " against "
                      <> describe_options(wanted),
                  ),
                  ..kept
                ])
              }
            True, _, _ -> #(text, removed, [
              #(name, "its options could not be read"),
              ..kept
            ])
          }
      }
    })
  let config = case included {
    True -> config
    False -> include_line <> "\n\n" <> config
  }
  SshUpdate(
    include_text: include_text,
    config_text: config,
    include_added: !included,
    removed: list.reverse(removed),
    kept: list.reverse(kept),
  )
}

/// The line numbers (first, last) of the first `Host` block whose `Host` line
/// names the alias literally, and that line's patterns.
fn hand_block_lines(
  text: String,
  name: String,
) -> Option(#(Int, Int, List(String))) {
  let lines = string.split(text, "\n")
  let numbered = list.index_map(lines, fn(line, index) { #(index, line) })
  case
    list.find(numbered, fn(pair) {
      case line_keyword(pair.1) {
        #("host", patterns) -> list.contains(patterns, name)
        _ -> False
      }
    })
  {
    Error(_) -> None
    Ok(#(from, line)) -> {
      let #(_, patterns) = line_keyword(line)
      let after =
        list.drop(numbered, from + 1)
        |> list.find(fn(pair) {
          case line_keyword(pair.1) {
            #("host", _) | #("match", _) -> True
            _ -> False
          }
        })
      let to = case after {
        Ok(#(next, _)) -> next - 1
        Error(_) -> list.length(lines) - 1
      }
      // The block ends at its last option line, not at the blank lines or
      // comments that introduce the next one.
      let to = last_option_line(lines, from, to)
      Some(#(from, to, patterns))
    }
  }
}

/// The highest index in `from`..`to` whose line is neither blank nor a
/// comment, or `from` when there is none. CHG 2026-09-27: a backward scan;
/// this built the index list, reversed it and searched it.
fn last_option_line(lines: List(String), from: Int, to: Int) -> Int {
  case to < from {
    True -> from
    False ->
      case list.drop(lines, to) {
        [candidate, ..] ->
          case string.trim(candidate) {
            "" | "#" <> _ -> last_option_line(lines, from, to - 1)
            _ -> to
          }
        [] -> last_option_line(lines, from, to - 1)
      }
  }
}

fn line_keyword(line: String) -> #(String, List(String)) {
  case ssh_lines(line) {
    [only] -> only
    _ -> #("", [])
  }
}

fn block_options(text: String, from: Int, to: Int) -> Dict(String, String) {
  string.split(text, "\n")
  |> list.drop(from + 1)
  |> list.take(to - from)
  |> string.join("\n")
  |> ssh_lines
  |> list.fold(dict.new(), fn(options, line) {
    case dict.has_key(options, line.0) {
      True -> options
      False -> dict.insert(options, line.0, string.join(line.1, " "))
    }
  })
}

fn same_options(
  wanted: Dict(String, String),
  found: Dict(String, String),
) -> Bool {
  wanted == found
}

fn describe_options(options: Dict(String, String)) -> String {
  options
  |> dict.to_list
  |> list.sort(fn(a, b) { string.compare(a.0, b.0) })
  |> list.map(fn(pair) { pair.0 <> " " <> pair.1 })
  |> string.join(", ")
}

fn drop_lines(text: String, from: Int, to: Int) -> String {
  string.split(text, "\n")
  |> list.index_map(fn(line, index) { #(index, line) })
  |> list.filter(fn(pair) { pair.0 < from || pair.0 > to })
  |> list.map(fn(pair) { pair.1 })
  |> string.join("\n")
}

// ── What the installer must keep (slice F6) ──────────────────────────────

/// Every path under one of `roots` that a loaded unit's start commands or
/// environment, or a Site's supervisor declaration, names: the installer's
/// cleanup keeps these whatever their age. Slice F6 of
/// docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started.
///
/// On 2026-09-26 the cleanup removed `fern-compile.888e83de…`, which the local
/// supervisor named in `--fern-compile` and in `FERN_COMPILE_BIN`, and three
/// cells failed before they ran. A word or a value is cut at every `:` and
/// `=`, so `KEY=PATH`, `--flag=PATH` and a `PYTHONPATH` of several
/// directories are each seen; a path with `:` or `=` in its own name is not. A root is a directory; a path is under
/// it when it starts with the root and a `/`. Each path is given once, in the
/// order first seen.
pub fn named_paths(
  loaded: List(LoadedUnit),
  declarations: List(Declaration),
  roots: List(String),
) -> List(String) {
  let from_units =
    list.flat_map(loaded, fn(unit) {
      list.append(
        list.flatten(unit.exec_start),
        list.map(unit.environment, fn(pair) { pair.1 }),
      )
    })
  let from_declarations =
    list.flat_map(declarations, fn(declaration) {
      [declaration.fern_run, ..option.values([declaration.fern_compile])]
    })
  list.append(from_units, from_declarations)
  |> list.flat_map(path_pieces)
  |> list.filter(fn(piece) { under_any(piece, roots) })
  |> list.unique
}

fn path_pieces(word: String) -> List(String) {
  string.split(word, ":")
  |> list.flat_map(fn(piece) { string.split(piece, "=") })
  |> list.map(fn(piece) {
    string.trim(piece)
    |> strip_quotes
  })
}

fn strip_quotes(piece: String) -> String {
  let unquoted = case string.starts_with(piece, "\"") {
    True -> string.drop_start(piece, 1)
    False -> piece
  }
  case string.ends_with(unquoted, "\"") {
    True -> string.drop_end(unquoted, 1)
    False -> unquoted
  }
}

fn under_any(piece: String, roots: List(String)) -> Bool {
  list.any(roots, fn(root) {
    let directory = case string.ends_with(root, "/") {
      True -> root
      False -> root <> "/"
    }
    string.starts_with(piece, directory)
  })
}

// ── Each SSH use and its connection (slice F4) ───────────────────────────

/// What a connection to a Site is for. Only the tunnel asks for the
/// forward; only the short uses may share a connection; the tunnel never
/// shares, because a shared connection belongs to whichever process dialled
/// first and ends every session riding on it when that process is stopped
/// (2026-08-30: a stale lane's stop ended a running remote check, exit 143).
pub type SshUse {
  /// Carries the Home to the Site; runs without a remote command (`-N`).
  Tunnel
  /// Starts or stops the Site's supervisor: one short command.
  SupervisorControl
  /// A read-only short command.
  Check
  /// Copies files to or from the Site.
  Copy
}

/// Whether the SSH entry the alias resolves to asks for the forward itself,
/// as every entry written before slice F4 does. A tunnel then adds none of its
/// own: the same forward asked twice fails the second time, and with
/// `ExitOnForwardFailure` the whole connection ends.
pub type EntryForm {
  EntryWithForward
  EntryWithoutForward
}

/// The `-o` options of one use, in the order they are written.
pub fn ssh_options(
  purpose: SshUse,
  declaration: Declaration,
  entry: EntryForm,
) -> List(#(String, String)) {
  let compression = case declaration.ssh_compression {
    True -> [#("Compression", "yes")]
    False -> []
  }
  case purpose {
    Tunnel ->
      list.flatten([
        [
          #("BatchMode", "yes"),
          #("ControlMaster", "no"),
          #("ControlPath", "none"),
        ],
        case entry {
          EntryWithForward -> []
          EntryWithoutForward -> [
            #(
              "RemoteForward",
              "127.0.0.1:"
                <> int.to_string(declaration.remote_forward.site_port)
                <> " 127.0.0.1:"
                <> int.to_string(declaration.remote_forward.home_port),
            ),
            #("ExitOnForwardFailure", "yes"),
          ]
        },
        [#("ServerAliveInterval", "20"), #("ServerAliveCountMax", "3")],
        compression,
      ])
    SupervisorControl | Check | Copy ->
      list.flatten([
        [#("BatchMode", "yes"), #("ClearAllForwardings", "yes")],
        case declaration.ssh_control_path {
          Some(path) -> [
            #("ControlMaster", "auto"),
            #("ControlPath", path),
            #("ControlPersist", "60"),
          ]
          None -> [#("ControlMaster", "no"), #("ControlPath", "none")]
        },
        compression,
      ])
  }
}

/// The whole `ssh` command of one use: the program, `-N` for the tunnel, the
/// options, the alias, and the remote command's words.
pub fn ssh_command(
  ssh_program: String,
  purpose: SshUse,
  declaration: Declaration,
  entry: EntryForm,
  remote: List(String),
) -> List(String) {
  list.flatten([
    [ssh_program],
    case purpose {
      Tunnel -> ["-N"]
      _ -> []
    },
    list.flat_map(ssh_options(purpose, declaration, entry), fn(pair) {
      ["-o", pair.0 <> "=" <> pair.1]
    }),
    [alias(declaration.site)],
    remote,
  ])
}

/// The tunnel's own unit on this machine: it carries the Home to the Site
/// and nothing else, and systemd opens it again when it closes.
pub fn tunnel_unit_name(site: String) -> String {
  "fern-tunnel-" <> site <> ".service"
}

pub fn render_tunnel_unit(
  declaration: Declaration,
  ssh_program: String,
  entry: EntryForm,
  provenance: String,
) -> String {
  "# "
  <> provenance
  <> "\n[Unit]\nDescription=SSH tunnel carrying the Fern Home to Site "
  <> declaration.site
  <> " (127.0.0.1:"
  <> int.to_string(declaration.remote_forward.site_port)
  <> " there, 127.0.0.1:"
  <> int.to_string(declaration.remote_forward.home_port)
  <> " here)\n\n[Service]\nType=simple\nExecStart="
  <> escape(string.join(
    list.map(
      ssh_command(ssh_program, Tunnel, declaration, entry, []),
      unit_word,
    ),
    " ",
  ))
  <> "\nRestart=always\nRestartSec=10\n\n[Install]\nWantedBy=default.target\n"
}

/// One word of a unit's command line as systemd reads it: a word with a space
/// or a quote in it is quoted, or systemd splits it (the forward's value
/// `127.0.0.1:8622 127.0.0.1:8621` is one word to `ssh -o`).
fn unit_word(word: String) -> String {
  case string.contains(word, " ") || string.contains(word, "\"") {
    True -> "\"" <> string.replace(word, "\"", "\\\"") <> "\""
    False -> word
  }
}
