using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Управление_Ботами_Discord
{
    public sealed partial class MainPage : Page
    {
        private StorageFile? _selectedFile;

        public MainPage()
        {
            InitializeComponent();
            TokenBox.Password = Environment.GetEnvironmentVariable("BOT_TOKEN") ?? string.Empty;
            ChannelIdBox.Text = Environment.GetEnvironmentVariable("CHANNEL_ID") ?? string.Empty;
        }

        private async void ChooseFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    _selectedFile = file;
                    FilePathTextBlock.Text = file.Path;
                    StatusTextBlock.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Не удалось выбрать файл: {ex.Message}");
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string token = TokenBox.Password.Trim();
            if (token.Length == 0)
            {
                SetStatus("Введите токен Discord-бота.");
                return;
            }

            if (!ulong.TryParse(ChannelIdBox.Text.Trim(), out ulong channelId) || channelId == 0)
            {
                SetStatus("Введите корректный числовой ID канала.");
                return;
            }

            if (_selectedFile is null)
            {
                SetStatus("Сначала выберите файл для публикации.");
                return;
            }

            string messageIdText = MessageIdBox.Text.Trim();
            ulong? messageId = null;
            if (messageIdText.Length > 0)
            {
                if (!ulong.TryParse(messageIdText, out ulong parsedMessageId) || parsedMessageId == 0)
                {
                    SetStatus("ID сообщения должен быть положительным числом.");
                    return;
                }

                messageId = parsedMessageId;
            }

            SendButton.IsEnabled = false;
            BusyIndicator.IsActive = true;
            SetStatus("Чтение файла и отправка в Discord…");

            try
            {
                string fileContent = await File.ReadAllTextAsync(_selectedFile.Path);
                ParsedDiscordContent parsedContent = DiscordFileParser.Parse(fileContent, FirstEmbedColorBox.Text);
                if (parsedContent.PlainText.Length == 0 && parsedContent.Embeds.Count == 0)
                {
                    throw new InvalidOperationException("Файл не содержит текста или embed-блоков для отправки.");
                }

                if (messageId.HasValue)
                {
                    await DiscordMessagePublisher.EditAsync(token, channelId, messageId.Value, parsedContent);
                    SetStatus("Сообщение успешно обновлено.");
                }
                else
                {
                    int sentCount = await DiscordMessagePublisher.PublishAsync(token, channelId, parsedContent);
                    SetStatus($"Публикация завершена. Отправлено сообщений: {sentCount}.");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Не удалось выполнить операцию: {ex.Message}");
            }
            finally
            {
                BusyIndicator.IsActive = false;
                SendButton.IsEnabled = true;
            }
        }

        private void SetStatus(string message)
        {
            StatusTextBlock.Text = message;
        }
    }
}
