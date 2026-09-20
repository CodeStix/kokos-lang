import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

// The extension only ever talks to a *locally built* Kokos.LanguageServer.dll — there's no packaged
// server binary yet, so this just finds whichever configuration was last built.
function findServerDll(): string | undefined {
  const repoRoot = path.resolve(__dirname, "..", "..", "..");
  const candidates = ["Debug", "Release"].map((config) =>
    path.join(
      repoRoot,
      "Kokos.LanguageServer",
      "bin",
      config,
      "net10.0",
      "Kokos.LanguageServer.dll"
    )
  );
  return candidates.find((candidate) => fs.existsSync(candidate));
}

export function activate(context: vscode.ExtensionContext) {
  const serverDll = findServerDll();
  if (!serverDll) {
    vscode.window.showErrorMessage(
      "Kokos: couldn't find Kokos.LanguageServer.dll. Run 'dotnet build Kokos.sln' first."
    );
    return;
  }

  const run = { command: "dotnet", args: [serverDll], transport: TransportKind.stdio };
  const serverOptions: ServerOptions = { run, debug: run };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "kokos" }],
  };

  client = new LanguageClient(
    "kokosLanguageServer",
    "Kokos Language Server",
    serverOptions,
    clientOptions
  );

  context.subscriptions.push(client);
  client.start();
}

export function deactivate(): Thenable<void> | undefined {
  return client?.stop();
}
