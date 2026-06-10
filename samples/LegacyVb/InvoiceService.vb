Imports System

Namespace LegacyVb
    ''' <summary>Money + branch complexity + public entry — the VB analog of the C# sample.</summary>
    Public Class InvoiceService
        Public Function CalculateVat(amount As Decimal, region As String, isExempt As Boolean) As Decimal
            If isExempt Then Return 0D
            Dim rate As Decimal
            Select Case region
                Case "NO"
                    rate = 0.25D
                Case "UK"
                    rate = 0.2D
                Case "DE"
                    rate = 0.19D
                Case Else
                    rate = 0D
            End Select
            If amount > 10000D AndAlso Not isExempt Then
                rate += 0.01D
            End If
            Return amount * rate
        End Function

        Public Function FormatDueDate(issued As DateTime) As String
            Return issued.AddDays(30).ToString("o")
        End Function

        Private Function Helper(x As Integer) As Integer
            Return x + 1
        End Function

        ' Calls CalculateVat — gives it a caller, so blast-radius (the call graph) is exercised.
        Public Function GrossTotal(amount As Decimal, region As String) As Decimal
            Return amount + CalculateVat(amount, region, False)
        End Function
    End Class
End Namespace
