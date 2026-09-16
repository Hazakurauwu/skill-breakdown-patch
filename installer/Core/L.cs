using System.Globalization;

namespace EnragedON.Setup.Core;

/// <summary>
/// UI text. Portuguese when Windows is in Portuguese, English everywhere else.
/// Exposed as static properties so XAML can bind with {x:Static core:L.Name}.
/// </summary>
public static class L
{
    public static bool Pt { get; } = Environment.GetEnvironmentVariable("ENRAGEDON_SETUP_LANG") is { Length: > 0 } forced
        ? forced.Equals("pt", StringComparison.OrdinalIgnoreCase)
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);

    static string T(string en, string pt) => Pt ? pt : en;

    // ---- window / brand ----
    public static string WindowTitle => "EnragedON Setup";
    public static string Product => T("Skill Breakdown Patch", "Patch Skill Breakdown");
    public static string VersionFmt => T("version {0}", "versão {0}");
    public static string Site => "enragedon.com";
    public static string WhatDoesItDo => T("What does this patch do?", "O que esse patch faz?");

    // ---- home ----
    public static string HomeTitle => T("Choose where to install", "Escolha onde instalar");
    public static string HomeLead => T("We found these ShinraMeter installs on your PC. Pick one and we handle the rest.",
                                       "Encontramos estes ShinraMeter no seu PC. Escolha um e o resto é com a gente.");
    public static string ScanningLead => T("Looking for ShinraMeter on your PC, this takes a few seconds.",
                                           "Procurando o ShinraMeter no seu PC, leva alguns segundos.");
    public static string Scanning => T("Scanning your PC...", "Procurando no seu PC...");
    public static string NothingLead => T("We couldn't find ShinraMeter automatically. Point us to the folder and we handle the rest.",
                                          "Não encontramos o ShinraMeter automaticamente. Mostre a pasta e o resto é com a gente.");
    public static string NothingCard => T("No ShinraMeter found yet", "Nenhum ShinraMeter encontrado ainda");
    public static string ChooseFolder => T("Choose folder", "Escolher pasta");
    public static string AddManual => T("+ My meter is somewhere else", "+ Meu meter está em outro lugar");
    public static string PickFolderTitle => T("Select your ShinraMeter or TeraToolbox folder", "Selecione a pasta do ShinraMeter ou do TeraToolbox");
    public static string NotFoundInFolder => T("No ShinraMeter found in that folder.", "Nenhum ShinraMeter encontrado nessa pasta.");

    public static string KindClassicPlus => "TERA Europe Classic+";
    public static string KindToolbox => "TeraToolbox";

    public static string PillNotInstalled => T("Not installed", "Não instalado");
    public static string PillUpToDateFmt => T("Up to date · v{0}", "Atualizado · v{0}");
    public static string PillUpdateFmt => T("v{0} installed · update available", "v{0} instalada · atualização disponível");
    public static string PillUpdateUnknown => T("Older version · update available", "Versão antiga · atualização disponível");

    public static string BtnInstall => T("Install", "Instalar");
    public static string BtnUpdate => T("Update", "Atualizar");
    public static string BtnReinstall => T("Reinstall", "Reinstalar");
    public static string BtnUninstall => T("Uninstall", "Desinstalar");

    // ---- close the game ----
    public static string CloseTitle => T("Close the game first", "Feche o jogo primeiro");
    public static string CloseLead => T("ShinraMeter is still running and its files are locked. Close these and we will continue on our own.",
                                        "O ShinraMeter ainda está aberto e os arquivos estão travados. Feche estes e a gente continua sozinho.");
    public static string Cancel => T("Cancel", "Cancelar");
    public static string Waiting => T("Waiting… this screen continues automatically once they close.",
                                      "Aguardando… esta tela continua sozinha quando eles fecharem.");

    // ---- progress ----
    public static string Installing => T("Installing", "Instalando");
    public static string Uninstalling => T("Uninstalling", "Desinstalando");
    public static string SubInstallFmt => T("installing {0}", "instalando {0}");
    public static string SubUpdateFmt => T("updating to {0}", "atualizando para {0}");
    public static string SubUninstall => T("removing the patch", "removendo o patch");
    public static string DontOpenGame => T("Don't open the game until this finishes", "Não abra o jogo até terminar");

    public static string StepBackup => T("Backing up your original files", "Fazendo backup dos seus arquivos originais");
    public static string StepBackupNote => T("kept for uninstall", "guardado para desinstalar");
    public static string StepPatch => T("Installing the Skill Breakdown patch", "Instalando o patch Skill Breakdown");
    public static string StepPackets => T("Turning off “Export packets logs”", "Desligando “Export packets logs”");
    public static string StepPacketsNote => T("fixes party buff tracking", "corrige os buffs da party");
    public static string StepAutoUpdate => T("Stopping auto-updates from undoing the patch", "Impedindo o auto-update de desfazer o patch");
    public static string StepVerify => T("Checking everything", "Conferindo tudo");
    public static string StepRestore => T("Restoring your original files", "Restaurando seus arquivos originais");
    public static string StepCleanup => T("Cleaning up backups", "Limpando os backups");
    public static string NoteNotNeeded => T("not needed here", "não precisa aqui");

    // ---- done ----
    public static string DoneTitle => T("You're all set", "Tudo pronto");
    public static string DoneLead => T("Open the game and clear a dungeon, your hit-by-hit breakdown shows up on the site.",
                                       "Abra o jogo e faça uma dungeon, o detalhamento golpe a golpe aparece no site.");
    public static string New1Bold => T("Attack direction", "Direção do ataque");
    public static string New1Rest => T(" for every hit: back, side and front", " em cada golpe: costas, lado e frente");
    public static string New2Bold => T("Target names", "Nome dos alvos");
    public static string New2Rest => T(", so adds and bosses are told apart", ", pra separar adds de bosses");
    public static string New3Bold => T("Fixed:", "Corrigido:");
    public static string New3Rest => T(" party buffs and boss debuffs no longer vanish after the first boss",
                                       " buffs da party e debuffs do boss não somem mais depois do primeiro boss");
    public static string Close => T("Close", "Fechar");
    public static string OpenSite => T("Open enragedon.com", "Abrir enragedon.com");

    // ---- uninstall ----
    public static string UninstallDlgTitleFmt => T("Uninstall from {0}?", "Desinstalar do {0}?");
    public static string UninstallDlgBody => T("Your original ShinraMeter files are restored from the backup made during install. Your settings stay as they are, and “Export packets logs” stays off.",
                                               "Os arquivos originais do ShinraMeter voltam do backup feito na instalação. Suas configurações continuam como estão, e o “Export packets logs” continua desligado.");
    public static string KeepIt => T("Keep it", "Manter");
    public static string RemovedTitle => T("Patch removed", "Patch removido");
    public static string RemovedLead => T("Your ShinraMeter is back to its original files.", "Seu ShinraMeter voltou aos arquivos originais.");
    public static string BackToList => T("Back to the list", "Voltar pra lista");

    // ---- errors ----
    public static string ErrorTitle => T("Something went wrong", "Algo deu errado");
    public static string ErrorNothingChanged => T("Your meter was left working, you can try again.", "Seu meter continua funcionando, você pode tentar de novo.");
    public static string TryAgain => T("Try again", "Tentar de novo");
    public static string ErrLocked => T("The meter is still open and locking its files. Close it completely and try again.",
                                        "O meter ainda está aberto e travando os arquivos. Feche ele por completo e tente de novo.");
    public static string ErrDenied => T("Windows blocked writing to this folder. Run EnragedON Setup as administrator and try again.",
                                        "O Windows bloqueou a escrita nessa pasta. Abra o EnragedON Setup como administrador e tente de novo.");
    public static string ErrNoBackup => T("The original backup was not found in this folder, so it can't be restored automatically. Reinstall ShinraMeter to get the original files back.",
                                          "O backup original não foi encontrado nessa pasta, então não dá pra restaurar sozinho. Reinstale o ShinraMeter pra ter os arquivos originais de volta.");
    public static string ErrVerify => T("The files didn't match after copying. Close the meter and try again.",
                                        "Os arquivos não bateram depois de copiar. Feche o meter e tente de novo.");
    public static string ErrAdminCancelled => T("Administrator permission is needed for this folder.", "É preciso permissão de administrador pra essa pasta.");
}
