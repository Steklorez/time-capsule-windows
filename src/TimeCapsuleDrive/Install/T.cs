using System.Globalization;

namespace TimeCapsuleDrive.Install;

/// <summary>User-visible texts of the setup program: Russian on a Russian Windows, English elsewhere.</summary>
static class T
{
    static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    static string P(string en, string ru) => Ru ? ru : en;

    public static string Title => P("Time Capsule Drive — setup", "Time Capsule Drive — настройка");
    public static string Intro => P(
        "Connects an Apple Time Capsule (or AirPort disk) to this computer as an ordinary drive, using Apple's own fast protocol (AFP).",
        "Подключает Apple Time Capsule (или диск AirPort) к этому компьютеру как обычный диск по быстрому протоколу Apple (AFP).");
    public static string Server => P("Device on the network:", "Устройство в сети:");
    public static string ServerHint => P(
        "Found automatically. If the list stays empty, type the IP address of the Time Capsule (your router or AirPort Utility shows it), e.g. 192.168.1.5",
        "Находится автоматически. Если список пуст, введите IP-адрес Time Capsule (его показывает роутер или AirPort-утилита), например 192.168.1.5");
    public static string Searching => P("Searching the network for a Time Capsule…", "Поиск Time Capsule в сети…");
    public static string FoundFmt => P("Found: {0}", "Найдено: {0}");
    public static string NotFound => P("Nothing found automatically — type the IP address above.", "Автоматически ничего не найдено — введите IP-адрес выше.");
    public static string Login => P("User name:", "Имя пользователя:");
    public static string Password => P("Password:", "Пароль:");
    public static string LoginHint => P(
        "If the disk is protected by a single password, leave the user name empty. Leave both empty for guest access.",
        "Если диск защищён одним паролем, имя пользователя оставьте пустым. Для гостевого доступа оставьте пустыми оба поля.");
    public static string Connect => P("Connect", "Подключиться");
    public static string Volume => P("Disk:", "Диск:");
    public static string Drive => P("Drive letter:", "Буква диска:");
    public static string ShareCheck => P("Share this drive with other computers on my network as:", "Раздавать этот диск другим компьютерам в моей сети под именем:");
    public static string ShareHint => P(
        "Other computers then open it as \\\\{0}\\{1} and need nothing installed. They sign in with a user account of this computer and get full access to the disk; file sharing is opened in the firewall for your home network.",
        "Тогда другие компьютеры открывают его как \\\\{0}\\{1}, ставить на них ничего не нужно. Вход — под учётной записью этого компьютера, доступ к диску полный; в брандмауэре открывается общий доступ к файлам для домашней сети.");
    public static string InstallButton => P("Install", "Установить");
    public static string UpdateButton => P("Apply", "Применить");
    public static string RemoveButton => P("Uninstall", "Удалить программу");
    public static string CloseButton => P("Close", "Закрыть");
    public static string Connecting => P("Connecting…", "Подключение…");
    public static string ConnectedFmt => P("Connected. Disks found: {0}", "Подключено. Найдено дисков: {0}");
    public static string NoVolumes => P("Connected, but the server shares no disks.", "Подключено, но сервер не раздаёт ни одного диска.");
    public static string WrongPassword => P("Wrong user name or password.", "Неверное имя пользователя или пароль.");
    public static string CannotReachFmt => P("Cannot reach {0}: {1}", "Не удаётся связаться с {0}: {1}");
    public static string NeedServer => P("Enter the address of the Time Capsule.", "Укажите адрес Time Capsule.");
    public static string NeedVolume => P("Press Connect and choose a disk first.", "Сначала нажмите «Подключиться» и выберите диск.");
    public static string NeedShareName => P("Enter a name for the share.", "Укажите имя общей папки.");
    public static string ConfirmRemove => P("Remove Time Capsule Drive from this computer? Files on the Time Capsule are not touched.",
        "Удалить Time Capsule Drive с этого компьютера? Файлы на Time Capsule не затрагиваются.");
    public static string InstalledFmt => P("Ready. The Time Capsule is drive {0}", "Готово. Time Capsule подключена как диск {0}");
    public static string FailedFmt => P("Failed: {0}", "Ошибка: {0}");

    public static string NeedAdmin => P("Administrator rights are required.", "Нужны права администратора.");
    public static string StepWinFsp => P("Installing WinFsp (the drive component)…", "Установка WinFsp (компонент для диска)…");
    public static string WinFspFailed => P("WinFsp could not be installed.", "Не удалось установить WinFsp.");
    public static string WinFspDownloadFailed => P(
        "Could not download WinFsp from {0} ({1}). Check the internet connection and run setup again.",
        "Не удалось скачать WinFsp с {0} ({1}). Проверьте подключение к интернету и запустите установку снова.");
    public static string WinFspBadHash => P("The WinFsp installer is not the expected file; installation stopped.",
        "Установщик WinFsp не совпадает с ожидаемым файлом; установка остановлена.");
    public static string StateConnected => P("Status: connected as drive {0}", "Состояние: подключено, диск {0}");
    public static string StateConnecting => P("Status: connecting to {0}…", "Состояние: подключение к {0}…");
    public static string StateUnreachable => P("Status: not connected — the Time Capsule at {0} does not answer. Check that it is switched on and on the same network.",
        "Состояние: не подключено — Time Capsule по адресу {0} не отвечает. Проверьте, что она включена и находится в той же сети.");
    public static string StateWrongPassword => P("Status: not connected — the Time Capsule refuses the user name or password. Enter them again below and press Apply.",
        "Состояние: не подключено — Time Capsule не принимает имя пользователя или пароль. Введите их заново ниже и нажмите «Применить».");
    public static string StateError => P("Status: not connected — {0}", "Состояние: не подключено — {0}");
    public static string StateStopped => P("Status: the drive is not running. Press Apply to start it.", "Состояние: диск не запущен. Нажмите «Применить», чтобы запустить.");
    public static string StateShadowed => P(
        "The drive is connected as {0}, but in your session that letter belongs to a network drive, which hides it. Choose another drive letter below and press Apply (or disconnect that network drive).",
        "Диск подключён как {0}, но в вашем сеансе эта буква занята сетевым диском, и он его заслоняет. Выберите ниже другую букву и нажмите «Применить» (или отключите тот сетевой диск).");
    public static string StepLogin => P("Checking the connection and the password…", "Проверка подключения и пароля…");
    public static string NoSuchVolume => P("The Time Capsule has no disk named \"{0}\". Available: {1}", "На Time Capsule нет диска «{0}». Доступны: {1}");
    public static string BadDrive => P("\"{0}\" is not a usable drive letter.", "«{0}» — недопустимая буква диска.");
    public static string DriveInUse => P("Drive {0} is already used by another disk. Choose a different letter.", "Буква {0} уже занята другим диском. Выберите другую.");
    public static string BadShareName => P("The share name may contain only Latin letters, digits, spaces and . _ - $ (up to 80 characters).",
        "Имя общей папки может содержать только латинские буквы, цифры, пробелы и знаки . _ - $ (до 80 символов).");
    public static string ShareNameTaken => P("This computer already shares something under the name \"{0}\". Choose a different name.",
        "На этом компьютере уже есть общая папка с именем «{0}». Выберите другое имя.");
    public static string RolledBack => P("The previous settings were restored.", "Прежние настройки восстановлены.");
    public static string StepStop => P("Stopping the running drive…", "Остановка работающего диска…");
    public static string StepFiles => P("Copying the program and saving settings…", "Копирование программы и сохранение настроек…");
    public static string StepTask => P("Setting up start with Windows…", "Настройка запуска вместе с Windows…");
    public static string StepMount => P("Connecting the drive…", "Подключение диска…");
    public static string MountFailed => P("The drive did not appear. Details are in {0}", "Диск не появился. Подробности в {0}");
    public static string StepShare => P("Sharing the drive as \"{0}\"…", "Открытие общего доступа «{0}»…");
    public static string ShareFailed => P("The drive is connected, but sharing it did not work. Details are in {0}",
        "Диск подключён, но открыть общий доступ не удалось. Подробности в {0}");
    public static string Done => P("Done.", "Готово.");
    public static string Removed => P("Time Capsule Drive has been removed.", "Time Capsule Drive удалена.");
}
