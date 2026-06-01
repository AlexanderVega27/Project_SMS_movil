using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Configuration;

namespace Serpost.WhatsApp.Monitor
{
    public partial class Form1 : Form
    {
        private bool _isExecuting = false;
        private int _enviadosHoy = 0;
        private int _fallidosHoy = 0;

        // Estructura para evitar que se procesen IDs duplicados mientras se espera el delay o la respuesta de red
        private static readonly HashSet<string> _idsEnProceso = new HashSet<string>();

        // Cliente HTTP único y reutilizable
        private static readonly HttpClient _httpClient = new HttpClient();

        // Variables globales para la configuración (Mejora de rendimiento)
        private readonly IConfigurationRoot _config;
        private readonly string _connStr;
        private readonly string _endpointGateway;

        public Form1()
        {
            InitializeComponent();

            // Leemos el appsettings.json UNA sola vez al iniciar la aplicación
            _config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            _connStr = _config.GetSection("ConnectionStrings")["OracleSOP"] ?? "";
            
            _endpointGateway = _config.GetSection("SMSConfig")["UrlGateway"] ?? "https://api.proveedor-sms.com/v1";
        }

        #region LOGS Y CONTADORES
        private void EscribirLog(string mensaje)
        {
            if (richTextBox1.InvokeRequired)
            {
                richTextBox1.Invoke(new Action(() => EscribirLog(mensaje)));
            }
            else
            {
                richTextBox1.AppendText($"[{DateTime.Now:HH:mm:ss}] {mensaje}{Environment.NewLine}");
                richTextBox1.SelectionStart = richTextBox1.Text.Length;
                richTextBox1.ScrollToCaret();
            }
        }

        private void ActualizarContadores()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(ActualizarContadores));
            }
            else
            {
                lblContadorExito.Text = _enviadosHoy.ToString();
                lblContadorFallo.Text = _fallidosHoy.ToString();
            }
        }
        #endregion


        #region LÓGICA PRINCIPAL
        private async Task MotorPrincipal()
        {
            while (_isExecuting)
            {
                try
                {
                    // --- CONTROL DE HORARIO OPERATIVO (7 AM - 9 PM) ---
                    int horaActual = DateTime.Now.Hour;
                    if (horaActual < 6 || horaActual >= 21)
                    {
                        if (DateTime.Now.Minute % 15 == 0)
                        {
                            EscribirLog("FUERA DE HORARIO: El bot de SMS dormirá unos minutos...");
                        }

                        // Espera dividida para reaccionar rápido al botón "Detener"
                        for (int i = 0; i < 60; i++)
                        {
                            if (!_isExecuting) return;
                            await Task.Delay(5000); // Tramos de 5 segundos (Total 5 min)
                        }
                        continue;
                    }

                    // Consultar Base de Datos Oracle
                    DataTable dt = ObtenerPendientes();

                    if (dt.Rows.Count > 0)
                    {
                        // 1. CAMBIO DE ESTADO TEMPORAL A 'B' (Bloqueado)
                        // Esto evita al 100% que otra instancia o consulta jale estos mismos trackings mientras se procesan
                        foreach (DataRow row in dt.Rows)
                        {
                            string id = row["ID_NOTIF"].ToString() ?? "";
                            string origen = row["ORIGEN"].ToString() ?? "";
                            ActualizarEstadoTemporal(id, "B", origen);
                        }

                        bool gatewayOperativo = true;
                        string idNotifActual = "";
                        string origenActual = "";

                        foreach (DataRow row in dt.Rows)
                        {
                            if (!_isExecuting) break;

                            idNotifActual = row["ID_NOTIF"].ToString() ?? "";
                            origenActual = row["ORIGEN"].ToString() ?? "";

                            // Envío directo vía HTTP al Gateway (Retorna FALSE si el equipo destino denegó la conexión)
                            gatewayOperativo = await ProcesarEnvioSMS(row);

                            ActualizarContadores();

                            // Si el equipo se cayó, rompemos el recorrido de este lote de inmediato
                            if (!gatewayOperativo)
                            {
                                break;
                            }

                            // --- DELAY DE SEGURIDAD (Simulación Física Módem) ---
                            Random rnd = new Random();
                            await Task.Delay(rnd.Next(5000, 8000));
                        }

                        // --- SI EL EQUIPO DENEGÓ LA CONEXIÓN: PAUSA DE 30 MINUTOS ---
                        if (!gatewayOperativo && _isExecuting)
                        {
                            EscribirLog("¡ALERTA!: Conexión denegada por el equipo destino. Liberando registros restantes del lote a 'P'...");

                            // IMPORTANTE: Como rompimos el bucle, los registros que NO se llegaron a procesar se quedaron en 'B'. 
                            // Los regresamos a 'P' para que no queden huérfanos.
                            foreach (DataRow row in dt.Rows)
                            {
                                string id = row["ID_NOTIF"].ToString() ?? "";
                                string origen = row["ORIGEN"].ToString() ?? "";
                                // Si el ID es igual o posterior al que falló, se devuelve a Pendiente 'P'
                                if (string.Compare(id, idNotifActual) >= 0)
                                {
                                    ActualizarEstadoDefinitivo(id, "P", "REINICIO POR CAÍDA DE GATEWAY", origen, incrementarIntento: false);
                                }
                            }

                            EscribirLog("El motor entrará en espera por 30 MINUTOS para evitar quemar intentos...");

                            // 1800 segundos = 30 minutos. Evaluamos segundo a segundo por si presionas "Detener"
                            for (int i = 0; i < 1800; i++)
                            {
                                if (!_isExecuting) return;
                                await Task.Delay(1000);
                            }
                            continue; // Volver a empezar el bucle principal
                        }
                    }

                    // Pausa de 20 segundos antes de verificar nuevas filas pendientes
                    if (_isExecuting) await Task.Delay(20000);
                }
                catch (Exception ex)
                {
                    EscribirLog("AVISO: Error en bucle principal. Reintentando en 10s... " + ex.Message);
                    await Task.Delay(10000);
                }
            }
        }

        private async Task<bool> ProcesarEnvioSMS(DataRow row)
        {
            string id = row["ID_NOTIF"].ToString() ?? "";
            string tel = row["TELEFONO"].ToString() ?? "";
            string nombres = row["NOMBRES"].ToString() ?? "";
            string tracking = row["COD_TRACKING"].ToString() ?? "";
            string origen = row["ORIGEN"].ToString() ?? "";

            string mensajeDinamico = row["MENSAJE"].ToString() ?? "";
            string oficinaDireccion = row["DIRECCION_OFICINA"].ToString() ?? "";
            string telefono = row["TELEFONO_WSP_OFICINA"].ToString() ?? "";
            string oficinaDestino = row["OFICINA_DESTINO"].ToString() ?? "Oficina Serpost";

            string mensajeSMS = "";

            switch (origen)
            {
                case "WSP_DOMICILIO":
                    mensajeSMS = $"SERPOST: ¡Tu envio esta a un paso, {nombres}! Confirmamos que tu paquete {tracking} ya salio de nuestra oficina y esta camino a tu domicilio. Recuerda siempre consultar el estado de tu envio en: https://www.serpost.com.pe/Cliente/SegumientoLinea. ¡Nos vemos en la puerta de tu casa!";
                    break;

                case "JOB_MASIVO":
                default:
                    mensajeSMS = $"SERPOST: {nombres}, tu envio {tracking} esta listo para recojo en {oficinaDestino} ubicado {oficinaDireccion} comuniquese al {telefono}. {mensajeDinamico} Mas info en www.serpost.com.pe/Cliente/SegumientoLinea";
                    break;
            }

            bool esErrorDeConexionEquipo = false;

            try
            {
                EscribirLog($"[{origen}] Enviando SMS vía Simple SMS Gateway: {tel}");

                var payload = new
                {
                    phone = tel.Trim(),
                    message = mensajeSMS
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();

                HttpResponseMessage response = await _httpClient.PostAsync(_endpointGateway, content);

                if (response.IsSuccessStatusCode)
                {
                    // Cambiado a ActualizarEstadoDefinitivo para limpiar el estado 'B'
                    ActualizarEstadoDefinitivo(id, "E", null, origen, incrementarIntento: false);
                    _enviadosHoy++;
                    EscribirLog($">>> ÉXITO SMS [{origen}]: Entregado al dispositivo para salida a {tel}");
                    return true;
                }
                else
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    throw new Exception($"El Gateway local respondió con error ({response.StatusCode}): {errorResponse}");
                }
            }
            catch (HttpRequestException httpEx)
            {
                string msgError = httpEx.Message.ToLower();
                if (msgError.Contains("denegó expresamente") || msgError.Contains("refused") || msgError.Contains("connection") || httpEx.InnerException is System.Net.Sockets.SocketException)
                {
                    esErrorDeConexionEquipo = true;
                }

                // Si es error del equipo físico, lo regresamos a 'P' (Pendiente) para que conserve sus intentos intactos.
                // Si es otro error HTTP de red genérico, va a 'F' (Fallo).
                string estadoDestino = esErrorDeConexionEquipo ? "P" : "F";
                string logError = esErrorDeConexionEquipo ? "CONEXIÓN DENEGADA EXPRESAMENTE (EQUIPO CAÍDO)" : httpEx.Message;

                ActualizarEstadoDefinitivo(id, estadoDestino, "ERROR RED: " + logError, origen, incrementarIntento: !esErrorDeConexionEquipo);
                _fallidosHoy++;
                EscribirLog($">>> PAUSA REQUERIDA [{origen}]: El equipo remoto no responde o rechazó la conexión.");

                return !esErrorDeConexionEquipo; // Retorna FALSE si es error crítico de equipo
            }
            catch (Exception ex)
            {
                ActualizarEstadoDefinitivo(id, "F", ex.Message, origen, incrementarIntento: true);
                _fallidosHoy++;
                EscribirLog($">>> FALLO SMS [{origen}]: " + ex.Message);
                return true;
            }
            finally
            {
                lock (_idsEnProceso)
                {
                    _idsEnProceso.Remove(id);
                }
            }
        }
        #endregion

        #region BASE DE DATOS ORACLE
        private void ActualizarEstadoTemporal(string id, string estado, string origen)
        {
            string tablaDestino = (origen == "WSP_DOMICILIO") ? "SOP.NOTIFICACIONES_WSP" : "SOP.NOTIFICACIONES_WSP_JOB";
            using (OracleConnection conn = new OracleConnection(_connStr))
            {
                string sql = $@"UPDATE {tablaDestino} SET ESTADO = :est WHERE ID_NOTIF = :id";
                conn.Open();
                using (OracleCommand cmd = new OracleCommand(sql, conn))
                {
                    cmd.BindByName = true;
                    cmd.Parameters.Add("est", estado);
                    cmd.Parameters.Add("id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void ActualizarEstadoDefinitivo(string id, string estado, string? error, string origen, bool incrementarIntento = false)
        {
            string tablaDestino = (origen == "WSP_DOMICILIO") ? "SOP.NOTIFICACIONES_WSP" : "SOP.NOTIFICACIONES_WSP_JOB";

            using (OracleConnection conn = new OracleConnection(_connStr))
            {
                string sql = $@"UPDATE {tablaDestino}
                               SET ESTADO = :est, FECHA_ENVIO = SYSDATE, ERROR_LOG = SUBSTR(:err, 1, 500)
                               {(incrementarIntento ? ", INTENTOS = NVL(INTENTOS, 0) + 1" : "")}
                               WHERE ID_NOTIF = :id";
                conn.Open();
                using (OracleCommand cmd = new OracleCommand(sql, conn))
                {
                    cmd.BindByName = true;
                    cmd.Parameters.Add("est", estado);
                    cmd.Parameters.Add("err", (object?)error ?? DBNull.Value);
                    cmd.Parameters.Add("id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private DataTable ObtenerPendientes()
        {
            using (OracleConnection conn = new OracleConnection(_connStr))
            {
                string query = @"
                    SELECT ID_NOTIF, TELEFONO, NOMBRES, COD_TRACKING, ORIGEN, MENSAJE, OFICINA_DESTINO, DIRECCION_OFICINA, TELEFONO_WSP_OFICINA
                    FROM (
                        SELECT ID_NOTIF, TELEFONO, NOMBRES, COD_TRACKING, ORIGEN, MENSAJE, OFICINA_DESTINO, DIRECCION_OFICINA, TELEFONO_WSP_OFICINA
                        FROM (
                            -- TABLA 2: Distribución a Domicilio (Prioridad Alta)
                            SELECT ID_NOTIF, TELEFONO, INITCAP(DESTINATARIO) AS NOMBRES, TRIM(TRACKING) AS COD_TRACKING, 'WSP_DOMICILIO' AS ORIGEN, 
                                   NULL AS MENSAJE, NULL AS OFICINA_DESTINO, NULL AS DIRECCION_OFICINA, NULL AS TELEFONO_WSP_OFICINA,
                                   1 AS PRIORIDAD
                            FROM SOP.NOTIFICACIONES_WSP
                            WHERE (ESTADO = 'P' OR (ESTADO = 'F' AND INTENTOS < 3))
                            
                            UNION ALL
                            
                            -- TABLA 1: Recojo en Oficina (Prioridad Baja / Masivo)
                            SELECT ID_NOTIF, TELEFONO, INITCAP(DESTINATARIO) AS NOMBRES, TRIM(TRACKING) AS COD_TRACKING, 'JOB_MASIVO' AS ORIGEN, 
                                   MENSAJE, OFICINA_DESTINO, DIRECCION_OFICINA, TELEFONO_WSP_OFICINA,
                                   2 AS PRIORIDAD
                            FROM SOP.NOTIFICACIONES_WSP_JOB
                            WHERE (ESTADO = 'P' OR (ESTADO = 'F' AND INTENTOS < 3))
                        )
                        ORDER BY PRIORIDAD ASC, ID_NOTIF ASC
                    )
                    WHERE ROWNUM <= 5";

                OracleDataAdapter da = new OracleDataAdapter(query, conn);
                DataTable dt = new DataTable();
                da.Fill(dt);
                return dt;
            }
        }
        #endregion


        #region BOTONES DE CONTROL
        private async void btnIniciar_Click(object sender, EventArgs e)
        {
            _isExecuting = true;
            btnIniciar.Enabled = false;
            btnDetener.Enabled = true;
            lblEstadoTexto.Text = "EJECUTANDO";
            lblEstadoTexto.ForeColor = Color.Green;

            EscribirLog("Iniciando motor de notificaciones por SMS local...");

            // Ahora el await funcionará sin mostrar error
            await Task.Run(MotorPrincipal);
        }

        private void btnDetener_Click(object sender, EventArgs e)
        {
            _isExecuting = false;
            btnIniciar.Enabled = true;
            btnDetener.Enabled = false;
            lblEstadoTexto.Text = "DETENIDO";
            lblEstadoTexto.ForeColor = Color.Red;

            EscribirLog("Servicio de SMS detenido por el usuario.");

            // Limpiamos la memoria al detener el servicio
            lock (_idsEnProceso)
            {
                _idsEnProceso.Clear();
            }
        }

        [DllImport("user32.DLL", EntryPoint = "ReleaseCapture")]
        private extern static void ReleaseCapture();

        [DllImport("user32.DLL", EntryPoint = "SendMessage")]
        private extern static void SendMessage(System.IntPtr hWnd, int wMsg, int wParam, int lParam);
        private void guna2Panel1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, 0x112, 0xf012, 0);
            }
        }
        #endregion
    }
}